<template>
  <div class="dashboard-page">
    <div class="stat-grid">
      <el-card v-for="item in stats" :key="item.label" shadow="never" class="stat-card">
        <div class="stat-card-inner">
          <span :class="['material-icons', 'stat-icon', item.iconClass]">{{ item.icon }}</span>
          <div>
            <div class="stat-label">{{ item.label }}</div>
            <div class="stat-value">{{ item.value }}</div>
          </div>
        </div>
      </el-card>
    </div>

    <el-row :gutter="12" class="mt-3 dashboard-row">
      <el-col :xs="24" :md="12">
        <el-card class="page-card dashboard-panel" shadow="never">
          <template #header>
            <span>快速操作</span>
          </template>
          <div class="quick-actions">
            <el-button type="primary" :icon="Upload" @click="router.push('/accounts/import')">导入账号</el-button>
            <el-button type="primary" :icon="Plus" @click="router.push('/channels/create')">创建频道</el-button>
            <el-button type="primary" :icon="Refresh" :loading="syncing" @click="syncAll">同步频道/群组数据</el-button>
          </div>
        </el-card>
      </el-col>

      <el-col :xs="24" :md="12">
        <el-card class="page-card dashboard-panel" shadow="never">
          <template #header>
            <span>系统状态</span>
          </template>
          <div class="status-list">
            <div class="status-row egress-status-row">
              <span :class="['status-dot', egress?.success ? 'ok' : (egress || egressError) ? 'danger' : 'warning']" />
              <div class="egress-status-content">
                <div>
                  面板公网出口：<strong>{{ egress?.success ? egress.ip || '未知' : (egress || egressError) ? '检测失败' : '检测中' }}</strong>
                  <el-tooltip v-if="egress?.warpStatus" :content="panelWarpStatusHelp" placement="top">
                    <el-tag size="small" :type="isWarpConnected(egress.warpStatus) ? 'success' : 'info'">
                      {{ warpStatusLabel(egress.warpStatus) }}
                    </el-tag>
                  </el-tooltip>
                </div>
                <div class="cell-sub">{{ egressDescription }}</div>
              </div>
              <el-button
                link
                :icon="Refresh"
                :loading="egressLoading"
                title="重新检测面板公网出口"
                @click="loadEgress"
              />
            </div>
            <div class="status-row">
              <span class="status-dot ok" />
              <span>正常账号: {{ summary?.normalAccountCount ?? '-' }}</span>
            </div>
            <div class="status-row">
              <span class="status-dot warn" />
              <span>受限账号: {{ summary?.limitedAccountCount ?? '-' }}</span>
            </div>
            <div class="status-row">
              <span class="status-dot danger" />
              <span>失效账号: {{ summary?.invalidAccountCount ?? '-' }}</span>
            </div>
          </div>
        </el-card>
      </el-col>
    </el-row>

    <el-card class="page-card mt-3 dashboard-task-card" shadow="never">
      <template #header>
        <span>最近任务</span>
      </template>
      <el-table v-loading="loading && !summary" :data="recentTaskRows" stripe row-key="id">
        <el-table-column label="任务名称" min-width="230">
          <template #default="{ row }">{{ taskName(row) }}</template>
        </el-table-column>
        <el-table-column label="类型" min-width="220">
          <template #default="{ row }">{{ fallbackTaskName(row.taskType) }}</template>
        </el-table-column>
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <StatusTag :status="displayStatus(row)" />
          </template>
        </el-table-column>
        <el-table-column label="进度" min-width="180">
          <template #default="{ row }">
            <el-progress :percentage="taskProgress(row)" :stroke-width="8" />
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="160">
          <template #default="{ row }">{{ formatRecentTime(row.createdAt) }}</template>
        </el-table-column>
        <template #empty>
          <el-empty description="暂无最近任务" />
        </template>
      </el-table>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { Plus, Refresh, Upload } from '@element-plus/icons-vue'
import { panelApi } from '@/api/panel'
import type { BatchTask, DashboardSummary, NetworkEgress } from '@/api/types'
import StatusTag from '@/components/StatusTag.vue'
import { taskProgress } from '@/utils/format'
import { ipVersionLabel, isWarpConnected, warpStatusLabel } from '@/utils/networkEgress'

const router = useRouter()
const loading = ref(false)
const syncing = ref(false)
const summary = ref<DashboardSummary | null>(null)
const egress = ref<NetworkEgress | null>(null)
const egressLoading = ref(false)
const egressError = ref('')
let timer: number | undefined
let loadPromise: Promise<void> | null = null

const stats = computed(() => [
  { label: '正常账号', value: summary.value?.normalAccountCount ?? '-', icon: 'verified_user', iconClass: 'primary' },
  { label: '受限账号', value: summary.value?.limitedAccountCount ?? '-', icon: 'warning', iconClass: 'warning' },
  { label: '失效账号', value: summary.value?.invalidAccountCount ?? '-', icon: 'person_off', iconClass: 'danger' },
  { label: '频道总数', value: summary.value?.channelCount ?? '-', icon: 'campaign', iconClass: 'secondary' },
])

const recentTaskRows = computed(() =>
  (summary.value?.recentTasks || [])
    .sort((a, b) => new Date(b.completedAt || b.createdAt).getTime() - new Date(a.completedAt || a.createdAt).getTime()),
)
const needsAutoRefresh = computed(() =>
  (summary.value?.activeTaskCount || 0) > 0
  || (summary.value?.enabledScheduledTaskCount || 0) > 0,
)
const egressDescription = computed(() => {
  if (egressError.value) return egressError.value
  if (!egress.value) return '正在检测出口信息'
  if (!egress.value.success) return egress.value.error || '无法获取出口信息'
  const location = [egress.value.country, egress.value.city, egress.value.isp].filter(Boolean).join(' / ')
  const latency = egress.value.latencyMs == null ? '' : `${egress.value.latencyMs} ms`
  return [ipVersionLabel(egress.value.ip), location, latency].filter(Boolean).join(' · ') || '位置未知'
})
const panelWarpStatusHelp = computed(() => {
  if (isWarpConnected(egress.value?.warpStatus)) {
    return '这里只表示面板服务自身的公网出口已使用 Cloudflare WARP。'
  }
  return '“未使用 WARP”只表示面板服务自身直连，不代表代理管理中的独立 WARP 失效。'
})

async function load(options: { silent?: boolean } = {}) {
  const showLoading = !options.silent
  if (showLoading) loading.value = true
  if (!loadPromise) {
    loadPromise = (async () => {
      summary.value = await panelApi.summary()
    })().finally(() => {
      loadPromise = null
    })
  }
  try {
    await loadPromise
  } finally {
    if (showLoading) loading.value = false
  }
}

async function syncAll() {
  if (syncing.value) {
    ElMessage.warning('正在同步中，请稍候...')
    return
  }
  syncing.value = true
  try {
    const result = await panelApi.startSyncNow()
    ElMessage.success(result.message || '同步完成')
    await load()
  } finally {
    syncing.value = false
  }
}

async function loadEgress() {
  egressLoading.value = true
  egressError.value = ''
  try {
    egress.value = await panelApi.networkEgress()
  } catch (error) {
    egress.value = null
    egressError.value = error instanceof Error ? error.message : '无法检测面板公网出口'
  } finally {
    egressLoading.value = false
  }
}

function displayStatus(task: BatchTask) {
  if (task.status === 'failed' && task.completedAt && task.total > 0 && task.completed >= task.total) return 'completed'
  return task.status
}

function taskName(task: BatchTask) {
  return task.name?.trim() || `${fallbackTaskName(task.taskType)} #${task.id}`
}

function fallbackTaskName(type: string) {
  if (type === 'user_join_subscribe') return '批量加群/订阅/启用Bot'
  if (type === 'bot_channel_set_admins_by_account') return 'Bot频道批量设置管理员（账号执行）'
  if (type === 'bot_set_admins') return 'Bot频道批量设置管理员（机器人执行）'
  if (type === 'user_chat_active') return '账号持续活跃（群组/频道/Bot）'
  if (type === 'account_auto_sync') return '账号数据同步'
  if (type === 'channel_group_private_create') return '批量创建私密频道/群组'
  if (type === 'channel_group_publicize') return '批量公开频道/群组'
  if (type === 'auto_change_login_email') return '自动更改登录邮箱'
  return type
}

function formatRecentTime(value?: string | null) {
  if (!value) return '-'
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return '-'
  const month = `${date.getMonth() + 1}`.padStart(2, '0')
  const day = `${date.getDate()}`.padStart(2, '0')
  const hour = `${date.getHours()}`.padStart(2, '0')
  const minute = `${date.getMinutes()}`.padStart(2, '0')
  return `${month}-${day} ${hour}:${minute}`
}

onMounted(() => {
  load()
  void loadEgress()
  timer = window.setInterval(() => {
    if (document.visibilityState === 'visible' && needsAutoRefresh.value) {
      void load({ silent: true }).catch(() => undefined)
    }
  }, 12000)
})
onUnmounted(() => {
  if (timer) window.clearInterval(timer)
})
</script>

<style scoped>
.dashboard-page {
  width: min(1152px, 100%);
  margin: 0 auto;
}

.dashboard-page :deep(.stat-grid) {
  width: 100%;
  gap: 12px;
}

.dashboard-page :deep(.stat-card) {
  border-color: #d7dce3;
  background: #fff;
  box-shadow: 0 2px 4px rgba(15, 23, 42, 0.16);
}

.dashboard-page :deep(.stat-card .el-card__body) {
  padding: 16px 18px;
}

.stat-card-inner {
  display: flex;
  align-items: center;
  gap: 14px;
}

.stat-icon {
  font-size: 30px;
  width: 36px;
  text-align: center;
}

.stat-icon.primary {
  color: var(--el-color-primary);
}

.stat-icon.secondary {
  color: #00bcd4;
}

.stat-icon.warning {
  color: #e6a23c;
}

.stat-icon.danger {
  color: #f56c6c;
}

.dashboard-panel {
  min-height: 116px;
  border-color: #d7dce3;
  background: #fff;
  box-shadow: 0 2px 4px rgba(15, 23, 42, 0.16);
}

.dashboard-panel :deep(.el-card__header),
.dashboard-task-card :deep(.el-card__header) {
  padding: 12px 14px;
  color: #1f2937;
  font-weight: 500;
  border-bottom-color: #d7dce3;
}

.dashboard-panel :deep(.el-card__body) {
  padding: 14px;
}

.dashboard-task-card {
  border-color: #d7dce3;
  background: #fff;
  box-shadow: 0 2px 4px rgba(15, 23, 42, 0.16);
}

.dashboard-task-card :deep(.el-card__body) {
  padding: 12px;
}

.quick-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
}

.status-list {
  display: grid;
  gap: 10px;
}

.status-row {
  display: flex;
  align-items: center;
  gap: 10px;
}

.egress-status-row {
  align-items: flex-start;
}

.egress-status-content {
  flex: 1;
  min-width: 0;
  overflow-wrap: anywhere;
}

.egress-status-content .el-tag {
  margin-left: 6px;
}

.status-dot {
  width: 8px;
  height: 8px;
  border-radius: 999px;
}

.status-dot.ok {
  background: #67c23a;
}

.status-dot.warn {
  background: #e6a23c;
}

.status-dot.danger {
  background: #f56c6c;
}

html.dark .dashboard-page :deep(.stat-card),
html.dark .dashboard-panel,
html.dark .dashboard-task-card {
  border-color: var(--tp-border);
  background: var(--tp-panel);
  box-shadow: var(--tp-card-shadow);
}

html.dark .dashboard-panel :deep(.el-card__header),
html.dark .dashboard-task-card :deep(.el-card__header) {
  color: var(--tp-text);
  border-bottom-color: var(--tp-border);
}
</style>
