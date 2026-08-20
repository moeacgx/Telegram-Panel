<template>
  <div class="account-import-page">
    <el-alert
      v-if="telegramApiChecked && !telegramApiConfigured"
      type="error"
      :closable="false"
      show-icon
      class="mb-3"
    >
      <template #title>Telegram API 当前不可用，Session 文件和 StringSession 暂不能导入。</template>
      <div class="import-api-warning">
        <span>当前生效 ApiId：{{ effectiveApiId || '未配置' }}</span>
        <el-button size="small" type="primary" @click="router.push('/settings')">去系统设置</el-button>
      </div>
    </el-alert>

    <section class="import-proxy-bar" aria-label="导入账号代理设置">
      <div class="import-proxy-heading">
        <span class="material-icons">vpn_lock</span>
        <div>
          <div class="cell-main">导入账号首次连接出口</div>
          <div class="cell-sub">必须先选择；在 Session 验证前生效</div>
        </div>
      </div>
      <el-radio-group
        v-model="proxyStrategy"
        class="proxy-strategy"
        aria-label="导入账号连接方式"
        :disabled="busy"
      >
        <el-radio-button value="existing">已有代理</el-radio-button>
        <el-radio-button value="proxy_per_account">批量代理一对一</el-radio-button>
        <el-radio-button value="warp_pool" :disabled="availableWarpPoolCount === 0">自动分配已有 WARP</el-radio-button>
        <el-radio-button value="warp_per_account" :disabled="!warpCreateAvailable">创建一对一 WARP</el-radio-button>
        <el-radio-button value="global">全局设置</el-radio-button>
        <el-radio-button value="direct">直连（确认风险）</el-radio-button>
      </el-radio-group>
      <el-select
        v-if="proxyStrategy === 'existing'"
        v-model="proxyId"
        filterable
        class="proxy-select"
        placeholder="选择已有代理"
        :disabled="busy"
      >
        <el-option
          v-for="proxy in proxies"
          :key="proxy.id"
          :value="proxy.id"
          :label="`${proxy.name} · ${proxy.protocol.toUpperCase()} · ${proxy.egressIp || `${proxy.host}:${proxy.port}`}`"
          :disabled="!proxy.isEnabled"
        />
      </el-select>
      <div v-if="!proxyStrategy" class="proxy-route-notice warning">
        为防止首个 Telegram 请求使用面板直连 IP，请明确选择已有代理或自动分配已有 WARP。
      </div>
      <div v-else-if="proxyStrategy === 'direct'" class="proxy-route-notice danger">
        已明确选择直连：Telegram 从首次验证开始即可看到面板公网 IP。
      </div>
      <div v-else-if="proxyStrategy === 'global'" class="proxy-route-notice warning">
        仅在已配置全局代理时可用；未配置会在首次连接前拒绝，请改选已有代理、自动分配已有 WARP 或明确直连。
      </div>
      <div v-else-if="proxyStrategy === 'warp_pool'" class="proxy-route-notice warning">
        将按当前账号绑定数自动选择已有 WARP；不会创建新容器。当前已启用 {{ availableWarpPoolCount }} 个 WARP。
      </div>
      <div v-else-if="proxyStrategy === 'warp_per_account'" class="proxy-route-notice warning">
        将为每个成功导入账号创建并绑定一个新的受管 WARP；单次最多 {{ WARP_PER_ACCOUNT_IMPORT_LIMIT }} 个账号，失败会在账号首次连接前停止。
      </div>
      <div v-else-if="proxyStrategy === 'proxy_per_account'" class="proxy-route-notice warning">
        批量代理一对一仅适用于 Zip 导入；Session 文件和 StringSession 导入在此模式下不可用。
      </div>
    </section>
    <section class="import-category-bar" aria-label="导入账号分类设置">
      <div class="import-proxy-heading">
        <span class="material-icons">category</span>
        <div>
          <div class="cell-main">导入后分类</div>
          <div class="cell-sub">可选；成功导入后直接归类</div>
        </div>
      </div>
      <el-select
        v-model="importCategoryId"
        clearable
        class="category-select"
        placeholder="未分类"
        :disabled="busy"
      >
        <el-option label="未分类" :value="null" />
        <el-option v-for="category in categories" :key="category.id" :label="category.name" :value="category.id" />
      </el-select>
    </section>
    <section class="import-category-bar" aria-label="导入账号设备指纹设置">
      <div class="import-proxy-heading">
        <span class="material-icons">fingerprint</span>
        <div>
          <div class="cell-main">导入后设备指纹</div>
          <div class="cell-sub">保存到账号，后续 Telegram 客户端按此画像连接</div>
        </div>
      </div>
      <el-select v-model="deviceProfileKey" class="category-select" filterable :disabled="busy">
        <el-option
          v-for="profile in deviceProfiles"
          :key="profile.key"
          :label="`${profile.name} · ${profile.family}`"
          :value="profile.key"
        />
      </el-select>
    </section>

    <el-card shadow="never" class="page-card import-card import-card-primary">
      <template #header>
        <div class="card-header">
          <span>压缩包导入（推荐）</span>
        </div>
      </template>
      <el-alert type="info" :closable="false" show-icon class="import-tip-alert">
        <template #title>
          <div>支持导入账号 Zip（推荐）：</div>
          <ul class="import-tips">
            <li>单账号：Zip 根目录直接包含一个 .json + 一个 .session</li>
            <li>批量导入：每个账号一个独立子文件夹，文件夹内包含一个 .json + 一个 .session</li>
            <li>tdata 协议包：支持 Zip 内包含 tdata 目录（含 key_datas / D877F783D5D3EF8C*）</li>
            <li>二级密码：自动解析账号目录下的 2fa.txt 文件作为二级密码保存到数据库</li>
          </ul>
          <div class="mt-2">提示：导入 tdata 会使用系统设置里的 Telegram API 池；未关闭内置官方 API 时可直接回退使用。</div>
        </template>
      </el-alert>
      <pre class="tree-example">xx.zip
├── 8613111111111
│   ├── 8613111111111.json
│   ├── 8613111111111.session
│   └── 2fa.txt
└── 8615119714541
    ├── 8615119714541.json
    └── 8615119714541.session</pre>
      <div class="upload-row">
        <el-upload
          v-model:file-list="zipUploadFiles"
          :auto-upload="false"
          :limit="1"
          accept=".zip"
          :on-change="onZipChange"
          :on-remove="onZipRemove"
          :disabled="busy"
        >
          <el-button type="primary" :icon="Upload" :disabled="busy">选择 Zip 压缩包</el-button>
        </el-upload>
        <span v-if="zipFile" class="muted">{{ zipFile.name }}（{{ formatBytes(zipFile.size) }}）</span>
      </div>
      <el-input
        v-model="zipTwoFactorPassword"
        type="password"
        show-password
        placeholder="二级密码（可选，用于没有 2fa.txt 的账号）"
        class="mt-4"
        :disabled="busy"
      />
      <div v-if="isPerAccountProxyBatch" class="batch-proxy-editor mt-4">
        <div class="batch-proxy-editor-heading">
          <div>
            <div class="cell-main">逐账号批量代理</div>
            <div class="cell-sub">仅支持 HTTP 和 SOCKS5，每行一个代理</div>
          </div>
          <el-tag :type="perAccountProxyLimitExceeded ? 'danger' : perAccountProxyCount > 0 ? 'primary' : 'info'" effect="plain">
            {{ perAccountProxyCount }} / {{ PER_ACCOUNT_PROXY_LIMIT }} 条有效代理
          </el-tag>
        </div>
        <el-input
          v-model="perAccountProxyText"
          type="textarea"
          :rows="8"
          resize="vertical"
          maxlength="100000"
          autocomplete="off"
          :spellcheck="false"
          placeholder="http://用户名:密码@主机:端口&#10;socks5://用户名:密码@主机:端口"
          :disabled="busy"
          aria-label="逐账号批量代理地址"
        />
        <ul class="batch-proxy-rules">
          <li>单次最多 100 条；空行和以 # 开头的注释行不计数，重复代理仍各占一个账号槽位。</li>
          <li>账号按 Zip 内规范化相对路径稳定排序，第 1 个账号对应第 1 条有效代理，账号数与代理数必须完全一致。</li>
          <li>系统先检测全部代理；全部成功后才新增代理并连接 Telegram，账号首次请求即使用对应代理，不会先直连再绑定。</li>
        </ul>
      </div>
      <el-button
        type="success"
        class="full-btn mt-4"
        :loading="importingZip"
        :disabled="busy || !zipFile || proxySelectionInvalid"
        @click="importZip"
      >
        {{ isPerAccountProxyBatch ? '检测代理并导入 Zip' : '开始导入 Zip' }}
      </el-button>
    </el-card>

    <div class="import-grid mt-4">
      <el-card shadow="never" class="page-card import-card">
        <template #header>
          <div class="card-header">
            <span>Session 文件导入</span>
          </div>
        </template>
        <el-upload
          v-model:file-list="sessionFiles"
          :auto-upload="false"
          multiple
          accept=".session"
          :on-change="onSessionChange"
          :on-remove="onSessionRemove"
          :disabled="busy || isPerAccountProxyBatch"
        >
          <el-button type="primary" :icon="UploadFilled" :disabled="busy || isPerAccountProxyBatch">选择 Session 文件</el-button>
        </el-upload>
        <div v-if="sessionFiles.length" class="file-list">
          <div v-for="file in sessionFiles" :key="file.uid" class="file-item">
            {{ file.name }}（{{ formatBytes(file.size || 0) }}）
          </div>
        </div>
        <el-button
          type="success"
          class="full-btn mt-4"
          :loading="importingSessions"
          :disabled="sessionImportDisabled"
          @click="importSessionFiles"
        >
          开始导入
        </el-button>
      </el-card>

      <el-card shadow="never" class="page-card import-card">
        <template #header>
          <div class="card-header">
            <span>StringSession 导入</span>
          </div>
        </template>
        <el-input
          v-model="sessionString"
          type="textarea"
          :rows="7"
          placeholder="Session String (Base64)"
          :disabled="busy || isPerAccountProxyBatch"
        />
        <el-button
          type="success"
          class="full-btn mt-4"
          :loading="importingString"
          :disabled="stringImportDisabled"
          @click="importStringSession"
        >
          导入 StringSession
        </el-button>
      </el-card>
    </div>

    <el-card v-if="importResults.length" shadow="never" class="page-card mt-4">
      <template #header>
        <div class="card-header">
          <span>导入结果</span>
          <el-button text :disabled="busy" @click="importResults = []">清空结果</el-button>
        </div>
      </template>
      <div v-if="importFeedbackSummary.partial > 0 || importFeedbackSummary.failed > 0" class="import-result-alerts">
        <el-alert
          v-if="importFeedbackSummary.partial > 0"
          type="warning"
          :closable="false"
          show-icon
          :title="`${importFeedbackSummary.partial} 个账号已导入，但代理设置失败`"
          description="账号数据已经保留，请查看下方消息列中的具体原因并重新设置代理。"
        />
        <el-alert
          v-if="importFeedbackSummary.failed > 0"
          type="error"
          :closable="false"
          show-icon
          :title="`${importFeedbackSummary.failed} 个账号导入失败`"
          description="请查看下方消息列中的具体错误。"
        />
      </div>
      <el-table :data="importResults" stripe>
        <el-table-column prop="phone" label="手机号" min-width="150">
          <template #default="{ row }">{{ row.phone || '-' }}</template>
        </el-table-column>
        <el-table-column prop="userId" label="用户ID" min-width="130">
          <template #default="{ row }">{{ row.userId || '-' }}</template>
        </el-table-column>
        <el-table-column prop="username" label="用户名" min-width="130">
          <template #default="{ row }">{{ row.username ? `@${row.username}` : '-' }}</template>
        </el-table-column>
        <el-table-column v-if="hasImportSourceDetails" prop="sourceKey" label="Zip 来源" min-width="180">
          <template #default="{ row }">{{ row.sourceKey || '-' }}</template>
        </el-table-column>
        <el-table-column v-if="hasImportProxyDetails" prop="proxyLine" label="代理行" width="90">
          <template #default="{ row }">{{ row.proxyLine ?? '-' }}</template>
        </el-table-column>
        <el-table-column v-if="hasImportProxyDetails" prop="proxyId" label="代理 ID" width="96">
          <template #default="{ row }">{{ row.proxyId ?? '-' }}</template>
        </el-table-column>
        <el-table-column v-if="hasImportProxyDetails" prop="proxyName" label="代理名称" min-width="150">
          <template #default="{ row }">{{ row.proxyName || '-' }}</template>
        </el-table-column>
        <el-table-column v-if="hasImportProxyDetails" prop="proxyEgressIp" label="出口 IP" min-width="150">
          <template #default="{ row }">{{ row.proxyEgressIp || '-' }}</template>
        </el-table-column>
        <el-table-column label="状态" width="104">
          <template #default="{ row }">
            <el-tag :type="getImportResultFeedback(row).tagType" size="small">
              {{ getImportResultFeedback(row).label }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="error" label="消息" min-width="260">
          <template #default="{ row }">{{ getImportResultFeedback(row).message }}</template>
        </el-table-column>
      </el-table>
    </el-card>

    <el-card v-if="rows.length" shadow="never" class="page-card mt-4">
      <template #header>
        <div class="card-header">
          <span>已导入账号（仅展示）</span>
          <div class="imported-account-actions">
            <el-button type="primary" :disabled="busy" @click="router.push('/accounts')">去账号列表操作</el-button>
            <el-button text :disabled="busy" @click="clearImported">清空</el-button>
          </div>
        </div>
      </template>

      <el-alert
        type="warning"
        :closable="false"
        show-icon
        class="mb-3"
        title="导入页不再提供批量操作"
        description="为避免新导入账号在当前页面直接执行敏感 Telegram 操作，导入后请先进入账号列表，再按分类、状态和代理出口筛选后操作。"
      />

      <el-table
        :data="rows"
        row-key="id"
        stripe
        class="mt-4"
      >
        <el-table-column prop="displayPhone" label="手机号" min-width="150" />
        <el-table-column prop="userId" label="用户ID" min-width="130" />
        <el-table-column prop="username" label="用户名" min-width="130">
          <template #default="{ row }">{{ row.username ? `@${row.username}` : '-' }}</template>
        </el-table-column>
        <el-table-column label="分类" min-width="130">
          <template #default="{ row }">
            <el-tag v-if="row.category" class="account-category-tag" effect="plain" :style="accountCategoryTagStyle(row.category)">
              {{ row.category.name }}
            </el-tag>
            <span v-else>未分类</span>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="86">
          <template #default="{ row }">
            <el-tag :type="row.isActive ? 'success' : 'info'" size="small">{{ row.isActive ? '活跃' : '停用' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="Telegram 状态" min-width="180">
          <template #default="{ row }">
            <el-tooltip v-if="row.telegramStatusSummary" :content="row.telegramStatusDetails || row.telegramStatusSummary" placement="top">
              <el-tag :type="telegramStatusTagType(row)" size="small">{{ telegramStatusText(row) }}</el-tag>
            </el-tooltip>
            <el-tag v-else type="info" size="small">未检测</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最后数据同步" min-width="170">
          <template #default="{ row }">{{ formatTime(row.lastSyncAt) }}</template>
        </el-table-column>
      </el-table>
    </el-card>

  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import type { UploadFile } from 'element-plus'
import { ElMessage } from 'element-plus'
import {
  Upload,
  UploadFilled,
} from '@element-plus/icons-vue'
import { panelApi } from '@/api/panel'
import type {
  AccountCategory,
  AccountListItem,
  AccountImportProxyStrategy,
  ImportAccountsResponse,
  ImportResult,
  OutboundProxy,
  TelegramDeviceProfile,
  WarpRuntimeStatus,
  ZipImportProxyStrategy,
} from '@/api/types'
import { formatTime } from '@/utils/format'
import { accountCategoryTagStyle } from '@/utils/categoryStyle'
import { getImportResultFeedback, summarizeImportResults } from '@/utils/importResultFeedback'
import { isInconclusiveTelegramStatus, isTransientTelegramStatus } from '@/utils/telegramStatus'

type Row = AccountListItem
const PER_ACCOUNT_PROXY_LIMIT = 100
const WARP_PER_ACCOUNT_IMPORT_LIMIT = 10

const router = useRouter()
const zipFile = ref<File | null>(null)
const zipUploadFiles = ref<UploadFile[]>([])
const zipTwoFactorPassword = ref('')
const perAccountProxyText = ref('')
const sessionFiles = ref<UploadFile[]>([])
const sessionString = ref('')
const importingZip = ref(false)
const importingSessions = ref(false)
const importingString = ref(false)

const importResults = ref<ImportResult[]>([])
const rows = ref<Row[]>([])
const categories = ref<AccountCategory[]>([])
const proxies = ref<OutboundProxy[]>([])
const proxyStrategy = ref<ZipImportProxyStrategy | ''>('')
const proxyId = ref<number | null>(null)
const telegramApiChecked = ref(false)
const telegramApiConfigured = ref(true)
const effectiveApiId = ref('')
const warpStatus = ref<WarpRuntimeStatus | null>(null)
const importCategoryId = ref<number | null>(null)
const deviceProfiles = ref<TelegramDeviceProfile[]>([])
const deviceProfileKey = ref('')
let importOperationToken = 0

const busy = computed(() => importingZip.value || importingSessions.value || importingString.value)
const shouldBlockApiImport = computed(() => telegramApiChecked.value && !telegramApiConfigured.value)
const availableWarpPoolCount = computed(() => proxies.value.filter(
  (proxy) => proxy.kind === 'warp'
    && proxy.isEnabled
    && proxy.warpRuntimeStatus === 'active',
).length)
const warpCreateAvailable = computed(() => Boolean(
  warpStatus.value?.platformSupported
    && warpStatus.value.enabled
    && warpStatus.value.dockerAvailable,
))
const isPerAccountProxyBatch = computed(() => proxyStrategy.value === 'proxy_per_account')
const perAccountProxyCount = computed(() => countEffectiveProxyLines(perAccountProxyText.value))
const perAccountProxyLimitExceeded = computed(() => perAccountProxyCount.value > PER_ACCOUNT_PROXY_LIMIT)
const proxySelectionInvalid = computed(() =>
  !proxyStrategy.value
  || (proxyStrategy.value === 'existing' && !proxyId.value)
  || (proxyStrategy.value === 'warp_pool' && availableWarpPoolCount.value === 0)
  || (proxyStrategy.value === 'warp_per_account' && !warpCreateAvailable.value)
  || (isPerAccountProxyBatch.value
    && (perAccountProxyCount.value === 0 || perAccountProxyLimitExceeded.value)),
)
const sessionImportDisabled = computed(() =>
  busy.value
  || isPerAccountProxyBatch.value
  || sessionFiles.value.length === 0
  || shouldBlockApiImport.value
  || proxySelectionInvalid.value,
)
const stringImportDisabled = computed(() =>
  busy.value
  || isPerAccountProxyBatch.value
  || !sessionString.value.trim()
  || shouldBlockApiImport.value
  || proxySelectionInvalid.value,
)
const importFeedbackSummary = computed(() => summarizeImportResults(importResults.value))
const hasImportSourceDetails = computed(() => importResults.value.some((result) => Boolean(result.sourceKey)))
const hasImportProxyDetails = computed(() => importResults.value.some((result) =>
  result.proxyLine != null
  || result.proxyId != null
  || Boolean(result.proxyName)
  || Boolean(result.proxyEgressIp),
))

function telegramStatusText(row: Row) {
  if (!row.telegramStatusSummary) return '未检测'
  if (!row.telegramStatusOk && isTransientTelegramStatus(row.telegramStatusSummary)) return '连接异常'
  if (!row.telegramStatusOk && isInconclusiveTelegramStatus(row.telegramStatusSummary)) return '检测异常'
  return row.telegramStatusOk ? row.telegramStatusSummary : '失效'
}

function telegramStatusTagType(row: Row) {
  if (!row.telegramStatusSummary) return 'info'
  if (!row.telegramStatusOk && isInconclusiveTelegramStatus(row.telegramStatusSummary)) return 'warning'
  return row.telegramStatusOk ? 'success' : 'danger'
}


function onZipChange(file: UploadFile, files: UploadFile[]) {
  zipUploadFiles.value = files.slice(-1)
  zipFile.value = file.raw || null
}

function onZipRemove() {
  zipUploadFiles.value = []
  zipFile.value = null
}

function onSessionChange(file: UploadFile, files: UploadFile[]) {
  sessionFiles.value = files
}

function onSessionRemove(_file: UploadFile, files: UploadFile[]) {
  sessionFiles.value = files
}


function countEffectiveProxyLines(text: string) {
  return text
    .replace(/\r\n?/g, '\n')
    .split('\n')
    .reduce((count, line) => {
      const normalized = line.trim()
      return normalized.length > 0 && !normalized.startsWith('#') ? count + 1 : count
    }, 0)
}

function ensureProxySelected(allowPerAccountBatch = false) {
  if (isPerAccountProxyBatch.value && !allowPerAccountBatch) {
    ElMessage.warning('批量代理一对一仅支持 Zip 导入，请更换代理方式')
    return false
  }
  if (!proxySelectionInvalid.value) return true
  if (!proxyStrategy.value) {
    ElMessage.warning('请先明确选择导入账号首次连接使用的代理方式')
  } else if (isPerAccountProxyBatch.value) {
    ElMessage.warning(perAccountProxyLimitExceeded.value
      ? `逐账号批量代理单次最多 ${PER_ACCOUNT_PROXY_LIMIT} 条`
      : '请填写逐账号批量代理，每行一个代理地址')
  } else {
    ElMessage.warning(proxyStrategy.value === 'warp_pool'
      ? '没有可自动分配的已有 WARP，请先在代理管理中准备并启用 WARP'
      : proxyStrategy.value === 'warp_per_account'
        ? warpStatus.value?.error || '当前环境无法创建 WARP，请先确认受管 WARP 运行环境'
        : '请选择已有代理')
  }
  return false
}

function appendProxyFields(
  form: FormData,
  strategy: AccountImportProxyStrategy,
  selectedProxyId: number | null,
) {
  form.append('proxyStrategy', strategy)
  if (strategy === 'existing' && selectedProxyId) {
    form.append('proxyId', String(selectedProxyId))
  }
}

function appendZipProxyFields(
  form: FormData,
  strategy: ZipImportProxyStrategy,
  selectedProxyId: number | null,
  selectedProxyText: string,
) {
  form.append('proxyStrategy', strategy)
  if (strategy === 'proxy_per_account') {
    form.append('proxyText', selectedProxyText)
  } else if (strategy === 'existing' && selectedProxyId) {
    form.append('proxyId', String(selectedProxyId))
  }
}

async function importZip() {
  if (busy.value) return
  if (!ensureProxySelected(true)) return
  if (!zipFile.value) {
    ElMessage.warning('请先选择 Zip 压缩包')
    return
  }

  const selectedZip = zipFile.value
  const selectedPassword = zipTwoFactorPassword.value
  const selectedStrategy = proxyStrategy.value as ZipImportProxyStrategy
  const selectedProxyId = proxyId.value
  const selectedProxyText = perAccountProxyText.value
  const form = new FormData()
  form.append('file', selectedZip)
  form.append('twoFactorPassword', selectedPassword)
  if (importCategoryId.value) form.append('categoryId', String(importCategoryId.value))
  if (deviceProfileKey.value) form.append('deviceProfileKey', deviceProfileKey.value)
  appendZipProxyFields(form, selectedStrategy, selectedProxyId, selectedProxyText)

  const operationToken = ++importOperationToken
  importingZip.value = true
  try {
    let response: ImportAccountsResponse
    try {
      response = await panelApi.importAccountsZip(form)
    } catch {
      // 响应拦截器已展示错误；禁止把含 proxyText 的 AxiosError 交给全局日志。
      return
    }
    if (operationToken !== importOperationToken) return
    applyImportResponse(response)
    if (zipFile.value === selectedZip) {
      zipFile.value = null
      zipUploadFiles.value = []
    }
    if (zipTwoFactorPassword.value === selectedPassword) zipTwoFactorPassword.value = ''
    if (selectedStrategy === 'proxy_per_account' && perAccountProxyText.value === selectedProxyText) {
      perAccountProxyText.value = ''
    }
  } finally {
    if (operationToken === importOperationToken) importingZip.value = false
  }
}

async function importSessionFiles() {
  if (busy.value) return
  if (!ensureTelegramApiConfigured()) return
  if (!ensureProxySelected()) return

  const selectedUploadFiles = [...sessionFiles.value]
  const files: File[] = []
  for (const uploadFile of selectedUploadFiles) {
    if (uploadFile.raw) files.push(uploadFile.raw as File)
  }
  if (files.length === 0) {
    ElMessage.warning('请先选择 Session 文件')
    return
  }

  const form = new FormData()
  files.forEach((file) => form.append('files', file))
  const selectedStrategy = proxyStrategy.value as AccountImportProxyStrategy
  const selectedProxyId = proxyId.value
  appendProxyFields(form, selectedStrategy, selectedProxyId)
  if (importCategoryId.value) form.append('categoryId', String(importCategoryId.value))
  if (deviceProfileKey.value) form.append('deviceProfileKey', deviceProfileKey.value)

  const operationToken = ++importOperationToken
  importingSessions.value = true
  try {
    const response = await panelApi.importAccountsSessionFiles(form)
    if (operationToken !== importOperationToken) return
    applyImportResponse(response)
    const selectionUnchanged = sessionFiles.value.length === selectedUploadFiles.length
      && sessionFiles.value.every((file, index) => file.uid === selectedUploadFiles[index]?.uid)
    if (selectionUnchanged) sessionFiles.value = []
  } finally {
    if (operationToken === importOperationToken) importingSessions.value = false
  }
}

async function importStringSession() {
  if (busy.value) return
  if (!ensureTelegramApiConfigured()) return
  if (!ensureProxySelected()) return

  if (!sessionString.value.trim()) {
    ElMessage.warning('请填写 StringSession')
    return
  }

  const selectedSessionString = sessionString.value
  const selectedStrategy = proxyStrategy.value as AccountImportProxyStrategy
  const selectedProxyId = proxyId.value
  const operationToken = ++importOperationToken
  importingString.value = true
  try {
    const response = await panelApi.importAccountsStringSession({
      sessionString: selectedSessionString,
      proxyStrategy: selectedStrategy,
      proxyId: selectedStrategy === 'existing' ? selectedProxyId : null,
      categoryId: importCategoryId.value,
      deviceProfileKey: deviceProfileKey.value || null,
    })
    if (operationToken !== importOperationToken) return
    applyImportResponse(response)
    if (response.results.some((x) => x.success) && sessionString.value === selectedSessionString) {
      sessionString.value = ''
    }
  } finally {
    if (operationToken === importOperationToken) importingString.value = false
  }
}

function applyImportResponse(response: ImportAccountsResponse) {
  importResults.value = response.results
  mergeImportedAccounts(response.accounts)

  const summary = summarizeImportResults(response.results)
  if (summary.succeeded > 0) ElMessage.success(`成功导入 ${summary.succeeded} 个账号`)
  if (summary.partial > 0) ElMessage.warning(`${summary.partial} 个账号已导入，但代理设置失败`)
  if (summary.failed > 0) ElMessage.error(`${summary.failed} 个账号导入失败`)
}

function mergeImportedAccounts(accounts: AccountListItem[]) {
  const map = new Map<number, Row>()
  rows.value.forEach((row) => map.set(row.id, row))
  accounts.forEach((account) => map.set(account.id, account))
  rows.value = Array.from(map.values()).sort((a, b) => b.id - a.id)
}

function clearImported() {
  rows.value = []
}

function ensureTelegramApiConfigured() {
  if (!shouldBlockApiImport.value) return true
  ElMessage.warning('请先配置全局 Telegram API')
  return false
}

function formatBytes(size: number) {
  if (size < 1024) return `${size} B`
  if (size < 1024 * 1024) return `${(size / 1024).toFixed(1)} KB`
  return `${(size / 1024 / 1024).toFixed(1)} MB`
}

async function loadCategories() {
  categories.value = await panelApi.accountCategories()
}

async function loadProxies() {
  proxies.value = await panelApi.proxies()
}

async function loadWarpStatus() {
  try {
    warpStatus.value = await panelApi.warpStatus()
  } catch {
    warpStatus.value = null
  }
}

async function loadTelegramApiStatus() {
  try {
    const settings = await panelApi.settings()
    deviceProfiles.value = settings.telegram.deviceProfiles || []
    deviceProfileKey.value = settings.telegram.defaultDeviceProfileKey || deviceProfiles.value[0]?.key || ''
    const apiId = (settings.telegram.apiId || '').trim()
    const apiHash = (settings.telegram.apiHash || '').trim()
    const enabledProfile = (settings.telegram.profiles || []).find((profile) => {
      const profileApiId = (profile.apiId || '').trim()
      const profileApiHash = (profile.apiHash || '').trim()
      return profile.enabled && !!profileApiId && !!profileApiHash
    })
    const profileApiId = (enabledProfile?.apiId || '').trim()
    const effectiveApiIdFromSettings = (settings.telegram.effectiveApiId || settings.system.effectiveApiId || '').trim()
    effectiveApiId.value = ((effectiveApiIdFromSettings !== '0' ? effectiveApiIdFromSettings : '') || (apiId !== '0' ? apiId : '') || profileApiId || '').trim()
    telegramApiConfigured.value = typeof settings.telegram.hasUsableApi === 'boolean'
      ? settings.telegram.hasUsableApi
      : ((!!apiId && apiId !== '0' && !!apiHash) || !!enabledProfile)
  } catch {
    telegramApiConfigured.value = true
  } finally {
    telegramApiChecked.value = true
  }
}

onMounted(() => {
  void Promise.allSettled([
    loadCategories(),
    loadProxies(),
    loadTelegramApiStatus(),
    loadWarpStatus(),
  ])
})

onBeforeUnmount(() => {
  // 代理认证信息只在当前导入会话中使用，离开页面后不保留在组件内存。
  perAccountProxyText.value = ''
})
</script>

<style scoped>
.account-import-page {
  min-width: 0;
}

.import-proxy-bar,
.import-category-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 14px;
  width: min(100%, 1160px);
  margin: 0 auto 16px;
  padding: 12px 14px;
  border: 1px solid var(--tp-border);
  border-left: 4px solid var(--el-color-primary);
  border-radius: 4px;
  background: var(--tp-panel);
  box-shadow: var(--tp-card-shadow);
}

.import-proxy-heading {
  display: flex;
  align-items: center;
  gap: 10px;
  min-width: 190px;
}

.import-proxy-heading .material-icons {
  color: var(--el-color-primary);
  font-size: 26px;
}

.proxy-strategy {
  min-width: 0;
  flex: 0 1 auto;
}

.proxy-select,
.category-select {
  width: min(360px, 100%);
}

.proxy-route-notice {
  flex-basis: 100%;
  padding-left: 36px;
  font-size: 13px;
  line-height: 1.5;
}

.proxy-route-notice.warning {
  color: var(--el-color-warning-dark-2);
}

.proxy-route-notice.danger {
  color: var(--el-color-danger);
}

.import-card {
  height: 100%;
}

.import-card :deep(.el-card__body) {
  display: flex;
  flex-direction: column;
}

.import-card-primary {
  width: min(100%, 1160px);
}

.import-tip-alert :deep(.el-alert__content) {
  width: 100%;
}

.batch-proxy-editor {
  display: grid;
  gap: 10px;
  padding: 14px;
  border: 1px solid var(--tp-border);
  border-left: 4px solid var(--el-color-warning);
  border-radius: 4px;
  background: var(--tp-panel-2);
}

.batch-proxy-editor-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
}

.batch-proxy-rules {
  margin: 0 0 0 18px;
  padding: 0;
  color: var(--tp-muted);
  font-size: 13px;
  line-height: 1.6;
}

.import-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
  width: min(100%, 1160px);
  margin-left: auto;
  margin-right: auto;
  align-items: stretch;
}

.tree-example {
  margin: 12px 0 0;
  padding: 12px;
  overflow: auto;
  border: 1px solid var(--tp-border);
  border-radius: 4px;
  color: var(--tp-text);
  background: var(--tp-code-bg);
}

.import-tips {
  margin: 6px 0 0 18px;
  padding: 0;
}

.upload-row {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 14px;
}

.full-btn {
  width: 100%;
}

.file-list {
  margin-top: 12px;
  max-height: 180px;
  overflow: auto;
  border: 1px solid var(--tp-border);
  border-radius: 4px;
  flex: 1;
}

.account-category-tag {
  border-radius: 999px;
}

.file-item {
  padding: 8px 10px;
  border-bottom: 1px solid var(--tp-border);
}

.file-item:last-child {
  border-bottom: 0;
}


.import-api-warning {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 6px;
}

.import-result-alerts {
  display: grid;
  gap: 8px;
  margin-bottom: 12px;
}

.imported-account-actions {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}


@media (max-width: 900px) {
  .import-grid {
    grid-template-columns: 1fr;
  }

  .import-proxy-bar,
  .import-category-bar {
    align-items: flex-start;
    flex-direction: column;
  }

  .proxy-strategy,
  .proxy-select,
  .category-select {
    width: 100%;
    max-width: 100%;
  }

  .proxy-strategy {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .proxy-strategy :deep(.el-radio-button),
  .proxy-strategy :deep(.el-radio-button__inner) {
    width: 100%;
    min-width: 0;
    padding: 8px 10px;
  }
}

@media (max-width: 360px) {
  .proxy-strategy {
    grid-template-columns: 1fr;
  }
}
</style>
