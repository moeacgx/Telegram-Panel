<template>
  <el-dialog
    v-model="visible"
    :title="operation === 'join' ? '批量加群/订阅/启用Bot' : '批量退群/退订/停用Bot'"
    width="640px"
    :close-on-click-modal="!submitting"
    :close-on-press-escape="!submitting"
    @closed="onClosed"
  >
    <el-form label-position="top">
      <el-alert
        :title="operation === 'join'
          ? '将使用所选账号执行批量加入/订阅；识别为 Bot 的目标会执行启用（发送 /start）。'
          : '将使用所选账号执行批量退群/退订；识别为 Bot 的目标会执行停用（拉黑 Bot）。'"
        type="warning"
        :closable="false"
        show-icon
      >
        <template #default>
          <div class="target-help">
            目标支持：https://t.me/xxx、t.me/+hash、@username、username、tg://join?invite=hash、
            t.me/bot_or_name?start=abc、@name?start=abc
          </div>
        </template>
      </el-alert>

      <div class="dialog-account">选中账号：{{ accountIds.length }} 个</div>

      <el-form-item label="目标列表（频道/群/Bot，换行分隔）">
        <el-input
          v-model="targetsText"
          type="textarea"
          :rows="10"
          :disabled="submitting"
          placeholder="每行一个目标。识别为 Bot 的目标将执行启用/停用；其余目标按频道/群处理。"
        />
      </el-form-item>

      <el-checkbox v-model="treatNoBotSuffixAsBot" :disabled="submitting">
        将不带 bot 后缀的用户名按 Bot 处理（用于纯用户名 Bot）
      </el-checkbox>

      <div v-if="treatNoBotSuffixAsBot" class="muted mt-2">
        已开启“无 bot 后缀按 Bot 处理”：如果同一列表中包含普通频道用户名，请关闭该选项避免误判。
      </div>

      <el-form-item label="操作间隔（毫秒）" class="mt-4">
        <el-input-number v-model="delayMs" :min="0" :max="60000" :step="500" :disabled="submitting" />
        <div class="field-help">建议设置 1500-4000ms；最高 60000ms，账号风控较高时可调大。</div>
      </el-form-item>

      <div v-if="operation === 'leave'" class="muted">
        说明：Bot 停用通过“拉黑 Bot”实现；如需恢复，可在 Telegram 内手动解除拉黑。
      </div>

      <el-alert class="mt-4" type="info" :closable="false" show-icon>
        提交后会创建后台任务，页面刷新、关闭弹窗或重新进入后台都不会中断执行。进度请到任务中心查看。
      </el-alert>
    </el-form>

    <template #footer>
      <el-button :disabled="submitting" @click="visible = false">关闭</el-button>
      <el-button type="primary" :loading="submitting" :disabled="accountIds.length === 0" @click="submit">
        {{ operation === 'join' ? '提交后台加群/订阅任务' : '提交后台退群/退订任务' }}
      </el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { panelApi } from '@/api/panel'
import { confirmChatMembershipRisk } from '@/utils/riskWarning'
import type { BatchTask } from '@/api/types'

const emit = defineEmits<{
  completed: [title: string, task: BatchTask]
}>()

const visible = ref(false)
const submitting = ref(false)
const operation = ref<'join' | 'leave'>('join')
const accountIds = ref<number[]>([])
const targetsText = ref('')
const treatNoBotSuffixAsBot = ref(false)
const delayMs = ref(2000)

function open(nextOperation: 'join' | 'leave', ids: number[]) {
  operation.value = nextOperation
  accountIds.value = Array.from(new Set(ids.filter((x) => x > 0)))
  targetsText.value = ''
  treatNoBotSuffixAsBot.value = false
  delayMs.value = 2000
  visible.value = true
}

defineExpose({ open })

async function submit() {
  if (submitting.value) return

  let targetAccounts = [...accountIds.value]
  const targets = parseTargets(targetsText.value)
  if (targetAccounts.length === 0) {
    ElMessage.info('请先选择账号')
    return
  }
  if (targets.length === 0) {
    ElMessage.warning('请至少输入一个目标')
    return
  }

  const parsed = targets.map((target) => parseTarget(target, treatNoBotSuffixAsBot.value))
  const membershipTargets = parsed.filter((target) => !target.isBot)
  const botTargets = parsed.filter((target) => target.isBot)

  if (membershipTargets.length > 0) {
    const riskAction = await confirmRisk(targetAccounts)
    if (riskAction === 'cancel') return
    if (riskAction.safeIds) {
      targetAccounts = riskAction.safeIds
      if (targetAccounts.length === 0) {
        ElMessage.info('排除风险账号后无剩余账号')
        return
      }
    }
  }

  const membershipTotal = membershipTargets.length * targetAccounts.length
  const botTotal = botTargets.length * targetAccounts.length
  const confirmLines: string[] = []
  if (membershipTotal > 0) {
    confirmLines.push(`${operation.value === 'join' ? '加群/订阅' : '退群/退订'}：${targetAccounts.length} 个账号 × ${membershipTargets.length} 个目标（共 ${membershipTotal} 次操作）`)
  }
  if (botTotal > 0) {
    confirmLines.push(`${operation.value === 'join' ? '启用' : '停用'}外部 Bot：${targetAccounts.length} 个账号 × ${botTargets.length} 个 Bot（共 ${botTotal} 次操作）`)
  }

  await ElMessageBox.confirm(`${confirmLines.join('\n')}\n\n是否继续？`, '确认执行', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })

  submitting.value = true
  try {
    const task = await panelApi.createChatMembershipTask({
      accountIds: targetAccounts,
      operation: operation.value,
      links: targets,
      treatNoBotSuffixAsBot: treatNoBotSuffixAsBot.value,
      delayMs: delayMs.value,
    })
    visible.value = false
    emit('completed', operation.value === 'join' ? '加群/订阅后台任务已提交' : '退群/退订后台任务已提交', task)
  } finally {
    submitting.value = false
  }
}

async function confirmRisk(ids: number[]): Promise<{ safeIds?: number[] } | 'cancel'> {
  const safeIds = await confirmChatMembershipRisk(ids)
  if (!safeIds) return 'cancel'
  if (safeIds.length === ids.length) return {}
  return { safeIds }
}

function onClosed() {
  if (!submitting.value) {
    targetsText.value = ''
  }
}

function parseTargets(text: string) {
  return text
    .split(/\r\n|\n|\r/)
    .map((x) => x.trim())
    .filter(Boolean)
    .filter((value, index, array) => array.findIndex((x) => x.toLowerCase() === value.toLowerCase()) === index)
}

function parseTarget(raw: string, assumePlainUsernameIsBot: boolean) {
  const isBot = shouldTreatAsBot(raw, assumePlainUsernameIsBot)
  return { raw, isBot }
}

function shouldTreatAsBot(raw: string, assumePlainUsernameIsBot: boolean) {
  if (/^tg:\/\/resolve/i.test(raw)) return true
  if (/[?&]start=/i.test(raw)) return true
  if (extractUsernameCandidate(raw).toLowerCase().endsWith('bot')) return true
  return assumePlainUsernameIsBot && looksLikeUsernameTarget(raw)
}

function looksLikeUsernameTarget(raw: string) {
  const value = raw.trim()
  if (!value) return false
  if (/^tg:\/\/join/i.test(value)) return false
  if (/joinchat\//i.test(value)) return false
  if (value.includes('/+')) return false
  const candidate = extractUsernameCandidate(value)
  if (candidate.startsWith('+')) return false
  return /^[A-Za-z0-9_]{5,64}$/.test(candidate)
}

function extractUsernameCandidate(raw: string) {
  let value = raw.trim()
  if (!value) return ''

  try {
    if (/^tg:\/\//i.test(value)) {
      const url = new URL(value)
      value = url.searchParams.get('domain') || value
    } else if (/^(https?:\/\/|t\.me\/|telegram\.me\/)/i.test(value)) {
      const url = new URL(value.includes('://') ? value : `https://${value}`)
      value = url.pathname.replace(/^\/+|\/+$/g, '').split('/')[0] || ''
    } else {
      value = value.replace(/^@+/, '')
    }
  } catch {
    value = value.replace(/^@+/, '')
  }

  return value.split('?')[0].split('/')[0].trim()
}

</script>

<style scoped>
.target-help {
  line-height: 1.6;
}

.dialog-account {
  margin: 12px 0;
  color: var(--tp-muted);
}

.mt-2 {
  margin-top: 8px;
}

.field-help {
  width: 100%;
  margin-top: 6px;
  color: var(--tp-muted);
  font-size: 12px;
}

</style>
