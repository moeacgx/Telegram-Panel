<template>
  <el-dialog v-model="dialog.visible" :title="`已绑定账号 · ${dialog.proxy?.name || ''}`" width="min(720px, calc(100vw - 24px))"
    :before-close="beforeClose" :close-on-click-modal="!dialog.running" :close-on-press-escape="!dialog.running" :show-close="!dialog.running">
    <el-alert v-if="dialog.error" :title="dialog.error" type="error" :closable="false" show-icon />
    <el-button :loading="dialog.loading" :disabled="dialog.running" @click="loadAccounts(dialog.page)">刷新账号</el-button>
    <el-table v-loading="dialog.loading" :data="dialog.accounts" empty-text="暂无专属绑定账号" class="bound-table">
      <el-table-column label="账号" min-width="170">
        <template #default="{ row }">
          <div>{{ row.remark || row.nickname || '未命名账号' }} (#{{ row.displayNumber || row.id }})</div>
          <div class="bound-meta">昵称：{{ row.nickname || '未设置' }} · 手机尾号 {{ row.phoneLastFour }}</div>
        </template>
      </el-table-column>
      <el-table-column label="当前代理" min-width="140">
        <template #default="{ row }">{{ dialog.proxy?.name }} (#{{ row.proxyId }})</template>
      </el-table-column>
      <el-table-column label="操作" width="120">
        <template #default="{ row }"><el-button link type="primary" :disabled="dialog.running || dialog.loading" @click="selectAccount(row)">解绑 / 换绑</el-button></template>
      </el-table-column>
    </el-table>
    <el-pagination v-if="dialog.total > 20" :current-page="dialog.page" :page-size="20" :total="dialog.total" :disabled="dialog.running || dialog.loading"
      layout="prev, pager, next" @current-change="loadAccounts" />
    <el-form v-if="dialog.selected" label-position="top" :disabled="dialog.running" class="bound-form">
      <el-form-item :label="`账号 #${dialog.selected.displayNumber || dialog.selected.id} 的解绑方式`" required>
        <el-select v-model="dialog.strategy" placeholder="请选择解绑后的出口" class="bound-full" @change="dialog.targetId = null">
          <el-option label="解绑并绑定到指定代理" value="existing" />
          <el-option label="解绑并使用全局代理" value="global" />
          <el-option label="解绑并直连" value="direct" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="dialog.strategy === 'existing'" label="目标代理" required>
        <el-select v-model="dialog.targetId" filterable :loading="dialog.targetsLoading" placeholder="选择目标代理" class="bound-full">
          <el-option v-for="proxy in eligibleTargets" :key="proxy.id" :value="proxy.id" :label="`${proxy.name} (#${proxy.id}) · ${proxy.egressIp || '未检测出口'}`" />
        </el-select>
        <div v-if="dialog.targetsError" class="bound-error" role="alert">{{ dialog.targetsError }}</div>
        <el-button link :disabled="dialog.running || dialog.targetsLoading" @click="loadTargets">刷新可选代理</el-button>
      </el-form-item>
      <el-alert v-if="dialog.strategy === 'direct'" title="确认后账号将使用面板服务器直接连接 Telegram。" type="warning" :closable="false" />
      <el-alert v-else-if="dialog.strategy === 'global'" title="确认后账号将使用全局代理；未配置可用全局代理时会拒绝解绑。" type="info" :closable="false" />
      <el-alert v-else-if="dialog.strategy === 'existing'" title="直接切换到所选代理；原 WARP 出口保留，解绑后可手动停止。" type="info" :closable="false" />
    </el-form>
    <template #footer>
      <el-button :disabled="dialog.running" @click="dialog.visible = false">关闭</el-button>
      <el-button v-if="dialog.selected" type="primary" :disabled="!canSubmit" :loading="dialog.running" @click="submit">确认{{ dialog.strategy === 'existing' ? '换绑' : '解绑' }}</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, reactive } from 'vue'
import { ElMessage } from 'element-plus'
import { onBeforeRouteLeave } from 'vue-router'
import { panelApi } from '@/api/panel'
import type { OutboundProxy, ProxyBoundAccount, WgcfRuntimeStatus } from '@/api/types'

const emit = defineEmits<{ changed: [] }>()
const dialog = reactive({ visible: false, running: false, loading: false, error: '', page: 1, total: 0,
  proxy: null as { id: number; name: string } | null, accounts: [] as ProxyBoundAccount[], selected: null as ProxyBoundAccount | null,
  strategy: '' as '' | 'direct' | 'global' | 'existing', targetId: null as number | null,
  targets: [] as OutboundProxy[], runtime: null as WgcfRuntimeStatus | null, targetsLoading: false, targetsError: '' })
let generation = 0
let accountsRequest = 0
let targetsRequest = 0
let disposed = false

const eligibleTargets = computed(() => dialog.targets.filter((proxy) => {
  if (proxy.id === dialog.proxy?.id || !proxy.isEnabled || proxy.kind === 'warp') return false
  if (!proxy.managedWgcfProfile) return proxy.kind !== 'wireguard_warp' || (proxy.testStatus === 'ok' && !!proxy.egressIp)
  const state = dialog.runtime?.profiles.find((item) => item.profile === proxy.managedWgcfProfile && item.proxyId === proxy.id)
  return !!dialog.runtime?.available && !!state?.poolEligible && state.phase === 'ready' && state.desired
    && state.runtime === 'listening' && state.testStatus === 'ok' && !!state.egressIp
    && state.accountCount === 0 && (proxy.accountCount ?? 0) === 0 && !proxy.isGlobal
}))
const canSubmit = computed(() => !dialog.running && !dialog.loading && !dialog.error && !!dialog.selected && !!dialog.strategy
  && (dialog.strategy !== 'existing' || (!dialog.targetsLoading && !dialog.targetsError && eligibleTargets.value.some((proxy) => proxy.id === dialog.targetId))))

function message(error: unknown) { return error instanceof Error ? error.message : '读取失败，请刷新后重试' }
function current(token: number) { return !disposed && token === generation && dialog.visible }

async function open(proxy: { id: number; name: string }) {
  if (dialog.running || proxy.id <= 0) return
  generation++
  Object.assign(dialog, { visible: true, proxy: { id: proxy.id, name: proxy.name }, accounts: [], total: 0, page: 1,
    selected: null, strategy: '', targetId: null, targets: [], runtime: null, error: '', targetsError: '' })
  await loadAccounts(1)
}

async function loadAccounts(page = 1) {
  if (!dialog.proxy || disposed) return
  const token = generation
  const request = ++accountsRequest
  const proxyId = dialog.proxy.id
  dialog.loading = true
  dialog.error = ''
  dialog.selected = null
  dialog.strategy = ''
  try {
    const result = await panelApi.proxyAccounts(proxyId, page)
    if (!current(token) || request !== accountsRequest) return
    const lastPage = Math.max(1, Math.ceil(result.total / result.pageSize))
    if (page > lastPage) { await loadAccounts(lastPage); return }
    dialog.accounts = result.items
    dialog.total = result.total
    dialog.page = result.page
  } catch (error) {
    if (current(token) && request === accountsRequest) { dialog.accounts = []; dialog.error = message(error) }
  } finally {
    if (token === generation && request === accountsRequest) dialog.loading = false
  }
}

async function loadTargets() {
  const token = generation
  const request = ++targetsRequest
  dialog.targetsLoading = true
  dialog.targetsError = ''
  try {
    const [proxies, runtime] = await Promise.allSettled([panelApi.proxies(), panelApi.wgcfStatus()])
    if (!current(token) || request !== targetsRequest) return
    if (proxies.status === 'rejected') throw proxies.reason
    dialog.targets = proxies.value
    // 运行器读取失败只关闭受管出口选择，普通代理仍可用于故障迁移。
    dialog.runtime = runtime.status === 'fulfilled' ? runtime.value : null
    if (!eligibleTargets.value.some((proxy) => proxy.id === dialog.targetId)) dialog.targetId = null
  } catch (error) {
    if (current(token) && request === targetsRequest) { dialog.targets = []; dialog.targetsError = message(error) }
  } finally {
    if (token === generation && request === targetsRequest) dialog.targetsLoading = false
  }
}

function selectAccount(account: ProxyBoundAccount) {
  if (dialog.running || dialog.loading || dialog.error || account.proxyId !== dialog.proxy?.id) return
  dialog.selected = { ...account }
  dialog.strategy = ''
  dialog.targetId = null
  void loadTargets()
}

async function submit() {
  if (!canSubmit.value || !dialog.selected) return
  const account = { ...dialog.selected }
  const strategy = dialog.strategy as 'direct' | 'global' | 'existing'
  const payload = { strategy, proxyId: strategy === 'existing' ? dialog.targetId : null,
    expectedProxyId: account.proxyId, expectedUseGlobalProxy: account.useGlobalProxy }
  dialog.running = true
  let failure = ''
  try {
    const result = await panelApi.setAccountProxy(account.id, payload)
    const item = result.items.find((entry) => entry.accountId === account.id)
    if (result.failed > 0 || !item?.success) failure = item?.error || item?.summary || '账号代理未切换，请核对当前绑定'
    else if (!disposed) ElMessage.success(strategy === 'existing' ? '账号已换绑到指定代理' : '账号已解除原代理绑定')
  } catch (error) {
    failure = `${message(error)}；已重新读取绑定，请核对后再操作`
  } finally {
    // 包括响应超时在内都重新回读，不使用旧快照再次提交。
    if (!disposed) {
      emit('changed')
      await loadAccounts(dialog.page)
      if (failure) dialog.error = [failure, dialog.error].filter(Boolean).join('；')
    }
    dialog.running = false
  }
}

function beforeClose(done: () => void) { if (!dialog.running) done() }
onBeforeUnmount(() => { disposed = true; generation++ })
onBeforeRouteLeave(() => !dialog.running)
defineExpose({ open })
</script>

<style scoped>
.bound-table, .bound-form { margin-top: 16px; }
.bound-meta { color: var(--el-text-color-secondary); font-size: 12px; overflow-wrap: anywhere; }
.bound-error { color: var(--el-color-danger); }
.bound-full { width: 100%; }
</style>
