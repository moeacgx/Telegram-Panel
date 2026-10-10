<template>
    <el-dialog v-model="createDialog.visible" title="一键创建 WARP" width="min(480px, calc(100vw - 24px))"
      :before-close="beforeCreateClose" :close-on-click-modal="!createDialog.running" :close-on-press-escape="!createDialog.running" :show-close="!createDialog.running">
      <el-form label-position="top" :disabled="createDialog.running">
        <el-form-item label="出口名称" required>
          <el-input v-model="createDialog.name" maxlength="80" :disabled="createDialog.submittedName !== null" placeholder="例如：账号专属出口" />
        </el-form-item>
        <el-checkbox v-model="createDialog.acceptTerms" :disabled="createDialog.submittedName !== null">我已阅读并接受</el-checkbox>
        <a class="wgcf-terms" href="https://www.cloudflare.com/application/terms/" target="_blank" rel="noopener noreferrer">Cloudflare WARP 条款</a>
      </el-form>
      <div v-if="createDialog.error" class="wgcf-error" role="alert">{{ createDialog.error }}</div>
      <template #footer>
        <el-button :disabled="createDialog.running" @click="createDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="createDialog.running" :disabled="!createDialog.acceptTerms || !createDialog.name.trim()" @click="createProfile">
          {{ createDialog.submittedName === null ? '创建' : '重试提交' }}
        </el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="bindDialog.visible" :title="`绑定账号 · ${bindDialog.profile?.name || ''}`" width="min(480px, calc(100vw - 24px))"
      :before-close="beforeBindClose" :close-on-click-modal="!bindDialog.running" :close-on-press-escape="!bindDialog.running" :show-close="!bindDialog.running">
      <el-form label-position="top" :disabled="bindDialog.running">
        <el-form-item label="账号" required>
          <el-select v-model="bindDialog.accountId" class="wgcf-full" filterable remote :remote-method="searchAccounts" :loading="bindDialog.searching" placeholder="搜索备注、昵称或手机号">
            <el-option v-for="account in bindDialog.accounts" :key="account.id" :value="account.id" :label="accountLabel(account)" />
          </el-select>
        </el-form-item>
        <div v-if="selectedAccount" class="wgcf-meta">当前代理：{{ selectedAccount.proxy ? `${selectedAccount.proxy.name} (#${selectedAccount.proxy.id})` : selectedAccount.useGlobalProxy ? '全局代理' : '直连' }}</div>
      </el-form>
      <div v-if="bindDialog.error" class="wgcf-error" role="alert">{{ bindDialog.error }}</div>
      <template #footer>
        <el-button :disabled="bindDialog.running" @click="bindDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="bindDialog.running" :disabled="!selectedAccount || !bindDialog.profile || !canBind(bindDialog.profile)" @click="bindAccount">绑定</el-button>
      </template>
    </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { panelApi } from '@/api/panel'
import type { AccountListItem, WgcfProfile, WgcfRuntimeStatus } from '@/api/types'

const emit = defineEmits<{ changed: []; state: [status: WgcfRuntimeStatus | null, error: string] }>()
const status = ref<WgcfRuntimeStatus | null>(null)
const profiles = computed(() => status.value?.profiles ?? [])
const loading = ref(false)
const loadError = ref('')
const busyProfiles = reactive(new Set<string>())
const createDialog = reactive({ visible: false, running: false, name: '', requestId: '', acceptTerms: false, submittedName: null as string | null, error: '' })
const bindDialog = reactive({ visible: false, running: false, searching: false, profile: null as WgcfProfile | null, accountId: null as number | null, accounts: [] as AccountListItem[], error: '' })
const selectedAccount = computed(() => bindDialog.accounts.find((account) => account.id === bindDialog.accountId))
let searchToken = 0
let profileRevision = 0
let disposed = false
let pollTimer: ReturnType<typeof setInterval> | null = null

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : '操作失败，请重试'
}

function operationUnavailable(profile: WgcfProfile) {
  return !status.value?.available || !!loadError.value || busyProfiles.has(profile.profile)
}

function canBind(profile: WgcfProfile) {
  return !operationUnavailable(profile) && profile.phase === 'ready' && profile.desired && profile.runtime === 'listening'
    && profile.testStatus === 'ok' && !!profile.egressIp && !!profile.proxyId && profile.accountCount === 0
}

function canOperate(profile: WgcfProfile, action: 'start' | 'stop' | 'resume' | 'test') {
  if (operationUnavailable(profile)) return false
  if (action === 'resume') return profile.phase === 'failed'
  if (action === 'start') return !profile.desired && profile.generated && profile.phase !== 'creating'
  if (action === 'stop') return profile.desired && profile.accountCount === 0 && profile.phase !== 'creating'
  return profile.desired && profile.runtime === 'listening' && profile.phase === 'ready'
}

function upsert(profile: WgcfProfile) {
  if (disposed) return
  profileRevision++
  const current = status.value ?? { available: true, reason: null, profiles: [] }
  status.value = {
    ...current,
    profiles: [...current.profiles.filter((item) => item.profile !== profile.profile), profile],
  }
  emit('state', status.value, loadError.value)
  emit('changed')
}

async function loadStatus() {
  if (loading.value || disposed) return
  loading.value = true
  const revision = profileRevision
  try {
    const next = await panelApi.wgcfStatus()
    // 轮询开始后的操作响应优先，避免旧快照抹掉刚创建或恢复的出口。
    if (disposed || revision !== profileRevision) return
    const changed = JSON.stringify(status.value?.profiles) !== JSON.stringify(next.profiles)
    status.value = next
    loadError.value = ''
    emit('state', next, '')
    if (changed) emit('changed')
  } catch (error) {
    if (!disposed) {
      loadError.value = errorMessage(error)
      emit('state', status.value, loadError.value)
    }
  } finally {
    if (!disposed) loading.value = false
  }
}

function openCreate() {
  if (!status.value?.available || loadError.value || createDialog.running) return
  Object.assign(createDialog, { visible: true, name: '', requestId: crypto.randomUUID(), acceptTerms: false, submittedName: null, error: '' })
}

defineExpose({ openCreate, loadStatus, operate, openBind, canBind, operationUnavailable, busyProfiles })

function beforeCreateClose(done: () => void) {
  if (!createDialog.running) done()
}

async function createProfile() {
  if (!status.value?.available || loadError.value || createDialog.running || !createDialog.acceptTerms || !createDialog.name.trim()) return
  createDialog.submittedName ??= createDialog.name.trim()
  const payload = { requestId: createDialog.requestId, name: createDialog.submittedName, acceptTerms: true }
  createDialog.running = true
  createDialog.error = ''
  try {
    const profile = await panelApi.createWgcfProfile(payload)
    upsert(profile)
    createDialog.visible = false
    ElMessage.success('出口创建已提交')
  } catch (error) {
    createDialog.error = errorMessage(error)
  } finally {
    createDialog.running = false
  }
}

async function operate(profile: WgcfProfile, action: 'start' | 'stop' | 'resume' | 'test') {
  if (!canOperate(profile, action)) return
  busyProfiles.add(profile.profile)
  try {
    const actions = { start: panelApi.startWgcfProfile, stop: panelApi.stopWgcfProfile, resume: panelApi.resumeWgcfProfile, test: panelApi.testWgcfProfile }
    const next = await actions[action](profile.profile)
    if (disposed) return
    upsert(next)
    if (action === 'test') {
      if (next.testStatus === 'ok' && next.egressIp) ElMessage.success(`检测通过：${next.egressIp}`)
      else ElMessage.warning(next.error || 'WARP 出口检测未通过')
    }
  } catch {
    // 接口错误由统一拦截器显示，刷新时保留原档案供重试。
  } finally {
    busyProfiles.delete(profile.profile)
  }
}

function accountLabel(account: AccountListItem) {
  return `${account.remark || account.nickname || account.username || '账号'} · ${account.displayPhone} (#${account.id})`
}

async function searchAccounts(search: string) {
  const token = ++searchToken
  bindDialog.searching = true
  try {
    const result = await panelApi.accounts({ page: 1, pageSize: 50, search })
    if (disposed || token !== searchToken || !bindDialog.visible) return
    const selected = selectedAccount.value
    bindDialog.accounts = selected && !result.items.some((account) => account.id === selected.id) ? [selected, ...result.items] : result.items
  } catch {
    // 保留已有选项，避免网络失败丢失明确选择的账号。
  } finally {
    if (token === searchToken && !disposed) bindDialog.searching = false
  }
}

function openBind(profile: WgcfProfile) {
  if (!canBind(profile) || bindDialog.running) return
  Object.assign(bindDialog, { visible: true, profile, accountId: null, accounts: [], error: '' })
  void searchAccounts('')
}

function beforeBindClose(done: () => void) {
  if (!bindDialog.running) done()
}

async function bindAccount() {
  const profile = profiles.value.find((item) => item.profile === bindDialog.profile?.profile) ?? bindDialog.profile
  const account = selectedAccount.value
  if (bindDialog.running || !profile || !canBind(profile) || !account) return
  bindDialog.running = true
  bindDialog.error = ''
  try {
    const result = await panelApi.setAccountProxy(account.id, {
      strategy: 'existing', proxyId: profile.proxyId, expectedProxyId: account.proxy?.id ?? 0, expectedUseGlobalProxy: account.useGlobalProxy,
    })
    if (disposed) return
    const item = result.items.find((entry) => entry.accountId === account.id)
    if (result.failed > 0 || !item?.success) {
      bindDialog.error = item?.error || item?.summary || '账号代理绑定失败'
      return
    }
    bindDialog.visible = false
    ElMessage.success(item.summary || '账号已绑定')
    emit('changed')
    await loadStatus()
  } catch (error) {
    if (!disposed) bindDialog.error = errorMessage(error)
  } finally {
    if (!disposed) bindDialog.running = false
  }
}

onMounted(() => {
  void loadStatus()
  pollTimer = setInterval(() => {
    if (document.visibilityState === 'visible' && !loadError.value && !createDialog.running && busyProfiles.size === 0 && !bindDialog.running) void loadStatus()
  }, 3000)
})

onBeforeUnmount(() => {
  disposed = true
  searchToken++
  if (pollTimer) clearInterval(pollTimer)
})
</script>

<style scoped>
.wgcf-meta { color: var(--el-text-color-secondary); font-size: 12px; margin-top: 4px; overflow-wrap: anywhere; }
.wgcf-error, .wgcf-row-error { color: var(--el-color-danger); font-size: 13px; overflow-wrap: anywhere; }
.wgcf-error { margin: 10px 0; }
.wgcf-terms { color: var(--el-color-primary); font-size: 14px; margin-left: 8px; overflow-wrap: anywhere; }
.wgcf-full { width: 100%; }
@media (max-width: 600px) { .wgcf-terms { display: block; margin-left: 0; } }
</style>
