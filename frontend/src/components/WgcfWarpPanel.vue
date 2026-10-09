<template>
  <section class="wgcf-panel" aria-label="轻量 WARP 出口">
    <header class="wgcf-header">
      <div class="wgcf-heading">
        <h2>轻量 WARP</h2>
        <el-tag :type="status?.available && !loadError ? 'success' : 'info'" size="small">
          {{ status?.available && !loadError ? '可用' : loading ? '读取中' : '不可用' }}
        </el-tag>
      </div>
      <div class="wgcf-actions">
        <el-button type="primary" :icon="CirclePlus" :disabled="!status?.available || !!loadError" @click="openCreate">创建出口</el-button>
        <el-tooltip content="刷新轻量 WARP 状态" placement="top">
          <el-button circle :icon="Refresh" :loading="loading" aria-label="刷新轻量 WARP 状态" @click="loadStatus" />
        </el-tooltip>
      </div>
    </header>
    <div v-if="loadError || status?.reason" class="wgcf-error" role="status">{{ loadError || status?.reason }}</div>
    <el-table :data="profiles" row-key="profile" class="wgcf-table" stripe>
      <el-table-column label="出口" min-width="150">
        <template #default="{ row }">
          <strong>{{ row.name }}</strong>
          <div class="wgcf-meta">{{ row.proxyId ? `代理 #${row.proxyId}` : '尚未生成代理' }}</div>
        </template>
      </el-table-column>
      <el-table-column label="运行状态" min-width="145">
        <template #default="{ row }">
          <el-tag :type="row.phase === 'failed' ? 'danger' : row.phase === 'ready' ? 'success' : 'info'" size="small">{{ phaseLabel(row) }}</el-tag>
          <div class="wgcf-meta">{{ runtimeLabel(row.runtime) }} · {{ row.desired ? '期望运行' : '期望停止' }}</div>
        </template>
      </el-table-column>
      <el-table-column label="联网检测" min-width="165">
        <template #default="{ row }">
          <div>{{ row.egressIp || '出口未检测' }}</div>
          <div class="wgcf-meta">{{ row.testStatus === 'ok' ? 'WARP 检测通过' : row.testStatus === 'failed' ? '检测失败' : '尚未通过检测' }}</div>
          <div v-if="row.error" class="wgcf-row-error">{{ row.error }}</div>
        </template>
      </el-table-column>
      <el-table-column label="绑定账号" width="95" align="center">
        <template #default="{ row }">{{ row.accountCount }}</template>
      </el-table-column>
      <el-table-column label="操作" width="158" fixed="right">
        <template #default="{ row }">
          <div class="wgcf-row-actions">
            <el-tooltip v-if="row.phase === 'failed'" content="继续创建此出口" placement="top">
              <el-button link type="warning" :icon="RefreshRight" :loading="busyProfiles.has(row.profile)" :disabled="operationUnavailable(row)" aria-label="继续创建此出口" @click="operate(row, 'resume')" />
            </el-tooltip>
            <el-tooltip v-else-if="!row.desired" content="启动出口" placement="top">
              <el-button link type="success" :icon="VideoPlay" :loading="busyProfiles.has(row.profile)" :disabled="operationUnavailable(row) || !row.generated || row.phase === 'creating'" aria-label="启动出口" @click="operate(row, 'start')" />
            </el-tooltip>
            <el-tooltip v-else :content="row.accountCount > 0 ? '请先在账号页面切换已绑定账号的代理' : '停止出口'" placement="top">
              <el-button link type="warning" :icon="SwitchButton" :loading="busyProfiles.has(row.profile)" :disabled="operationUnavailable(row) || row.accountCount > 0 || row.phase === 'creating'" aria-label="停止出口" @click="operate(row, 'stop')" />
            </el-tooltip>
            <el-tooltip content="检测 WARP 出口" placement="top">
              <el-button link type="primary" :icon="Connection" :disabled="operationUnavailable(row) || !row.desired || row.runtime !== 'listening' || row.phase !== 'ready'" aria-label="检测 WARP 出口" @click="operate(row, 'test')" />
            </el-tooltip>
            <el-tooltip content="绑定单个账号" placement="top">
              <el-button link type="primary" :icon="User" :disabled="!canBind(row)" aria-label="绑定单个账号" @click="openBind(row)" />
            </el-tooltip>
          </div>
        </template>
      </el-table-column>
      <template #empty><el-empty description="暂无轻量 WARP 出口" :image-size="48" /></template>
    </el-table>

    <el-dialog v-model="createDialog.visible" title="创建轻量 WARP 出口" width="min(480px, calc(100vw - 24px))"
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
  </section>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { CirclePlus, Connection, Refresh, RefreshRight, SwitchButton, User, VideoPlay } from '@element-plus/icons-vue'
import { panelApi } from '@/api/panel'
import type { AccountListItem, WgcfProfile, WgcfRuntimeStatus } from '@/api/types'

const emit = defineEmits<{ changed: [] }>()
const status = ref<WgcfRuntimeStatus | null>(null)
const profiles = computed(() => status.value?.profiles ?? [])
const loading = ref(false)
const loadError = ref('')
const busyProfiles = reactive(new Set<string>())
const createDialog = reactive({ visible: false, running: false, name: '', requestId: '', acceptTerms: false, submittedName: null as string | null, error: '' })
const bindDialog = reactive({ visible: false, running: false, searching: false, profile: null as WgcfProfile | null, accountId: null as number | null, accounts: [] as AccountListItem[], error: '' })
const selectedAccount = computed(() => bindDialog.accounts.find((account) => account.id === bindDialog.accountId))
let searchToken = 0
let disposed = false
let pollTimer: ReturnType<typeof setInterval> | null = null

function errorMessage(error: unknown) {
  return error instanceof Error ? error.message : '操作失败，请重试'
}

function phaseLabel(profile: WgcfProfile) {
  return { creating: '创建中', ready: '已就绪', failed: '创建失败', stopped: '已停止', starting: '启动中' }[profile.phase] || '未知状态'
}

function runtimeLabel(runtime: string) {
  return ({ listening: '进程监听中', stopped: '进程已停止', starting: '进程启动中', failed: '进程异常', pending: '等待执行', unknown: '进程状态未知' } as Record<string, string>)[runtime] || '进程状态未知'
}

function operationUnavailable(profile: WgcfProfile) {
  return !status.value?.available || !!loadError.value || busyProfiles.has(profile.profile)
}

function canBind(profile: WgcfProfile) {
  return !operationUnavailable(profile) && profile.phase === 'ready' && profile.desired && profile.runtime === 'listening'
    && profile.testStatus === 'ok' && !!profile.egressIp && !!profile.proxyId
}

function upsert(profile: WgcfProfile) {
  if (disposed) return
  const current = status.value ?? { available: true, reason: null, profiles: [] }
  status.value = {
    ...current,
    profiles: [...current.profiles.filter((item) => item.profile !== profile.profile), profile],
  }
  emit('changed')
}

async function loadStatus() {
  if (loading.value || disposed) return
  loading.value = true
  try {
    const next = await panelApi.wgcfStatus()
    if (disposed) return
    const changed = JSON.stringify(status.value?.profiles) !== JSON.stringify(next.profiles)
    status.value = next
    loadError.value = ''
    if (changed) emit('changed')
  } catch (error) {
    if (!disposed) loadError.value = errorMessage(error)
  } finally {
    if (!disposed) loading.value = false
  }
}

function openCreate() {
  if (!status.value?.available || loadError.value || createDialog.running) return
  Object.assign(createDialog, { visible: true, name: '', requestId: crypto.randomUUID(), acceptTerms: false, submittedName: null, error: '' })
}

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
  if (operationUnavailable(profile) || (action === 'stop' && profile.accountCount > 0)) return
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
.wgcf-panel { margin-top: 20px; padding: 16px 0; border-top: 1px solid var(--el-border-color-light); border-bottom: 1px solid var(--el-border-color-light); }
.wgcf-header, .wgcf-heading, .wgcf-actions, .wgcf-row-actions { display: flex; align-items: center; gap: 10px; }
.wgcf-header { justify-content: space-between; flex-wrap: wrap; margin-bottom: 12px; }
.wgcf-heading h2 { margin: 0; font-size: 17px; font-weight: 600; }
.wgcf-meta { color: var(--el-text-color-secondary); font-size: 12px; margin-top: 4px; overflow-wrap: anywhere; }
.wgcf-error, .wgcf-row-error { color: var(--el-color-danger); font-size: 13px; overflow-wrap: anywhere; }
.wgcf-error { margin: 10px 0; }
.wgcf-table { width: 100%; }
.wgcf-row-actions { justify-content: flex-end; gap: 6px; min-height: 32px; }
.wgcf-row-actions :deep(.el-button + .el-button) { margin-left: 0; }
.wgcf-terms { color: var(--el-color-primary); font-size: 14px; margin-left: 8px; overflow-wrap: anywhere; }
.wgcf-full { width: 100%; }
@media (max-width: 600px) { .wgcf-header { gap: 12px; } .wgcf-terms { display: block; margin-left: 0; } }
</style>
