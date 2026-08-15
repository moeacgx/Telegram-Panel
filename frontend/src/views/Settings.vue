<template>
  <div>
    <el-alert
      class="mb-4"
      type="info"
      :closable="false"
      show-icon
      :title="`写入位置：${settings?.localConfigPath || '-'}`"
    />

    <div class="settings-columns">
      <div class="settings-column">
        <el-card shadow="never" class="page-card">
          <template #header>Telegram API 配置</template>
          <el-form label-position="top">
            <el-form-item label="默认 API ID">
              <el-input v-model="telegram.apiId" />
              <div class="muted mt-2">从 https://my.telegram.org 获取。</div>
            </el-form-item>
            <el-form-item label="默认 API Hash">
              <el-input v-model="telegram.apiHash" />
              <div class="muted mt-2">兼容旧配置；未启用 API 配置池时用于新账号登录和导入。</div>
            </el-form-item>
            <el-divider />
            <div class="section-title">API 配置池</div>
            <el-alert type="info" :closable="false" show-icon class="mb-3">
              <template #title>新账号登录、Session 文件、StringSession 和 tdata 导入会在启用的配置间按已保存账号数均衡分配；Zip 内自带 api_id/api_hash 的账号保持包内配置。</template>
            </el-alert>
            <div v-for="(profile, index) in telegram.profiles" :key="index" class="api-profile-row">
              <el-row :gutter="8">
                <el-col :xs="24" :sm="6">
                  <el-form-item label="名称">
                    <el-input v-model="profile.name" placeholder="主 API" />
                  </el-form-item>
                </el-col>
                <el-col :xs="24" :sm="5">
                  <el-form-item label="ApiId">
                    <el-input v-model="profile.apiId" />
                  </el-form-item>
                </el-col>
                <el-col :xs="24" :sm="7">
                  <el-form-item label="ApiHash">
                    <el-input v-model="profile.apiHash" />
                  </el-form-item>
                </el-col>
                <el-col :xs="12" :sm="3">
                  <el-form-item label="权重">
                    <el-input-number v-model="profile.weight" :min="1" :max="1000" class="full" />
                  </el-form-item>
                </el-col>
                <el-col :xs="12" :sm="3">
                  <el-form-item label="启用">
                    <el-switch v-model="profile.enabled" />
                  </el-form-item>
                </el-col>
              </el-row>
              <el-form-item label="备注">
                <el-input v-model="profile.notes" placeholder="可选" />
              </el-form-item>
              <el-button text type="danger" @click="removeApiProfile(index)">删除此配置</el-button>
            </div>
            <el-button plain @click="addApiProfile">添加 API 配置</el-button>
          </el-form>
          <el-alert type="info" :closable="false" show-icon class="mb-3">
            <template #title>Telegram API 状态</template>
            <div>写入位置：{{ settings?.localConfigPath || '-' }}</div>
            <div>文件存在：{{ settings?.localConfigExists ? '是' : '否' }}</div>
            <div>当前生效 ApiId：{{ settings?.system.effectiveApiId || '（未配置）' }}</div>
          </el-alert>
          <el-button type="primary" :loading="saving.telegram" @click="saveTelegram">保存配置</el-button>
        </el-card>

        <el-card shadow="never" class="page-card">
          <template #header>
            <div class="card-header">
              <span>Cloud Mail 对接</span>
              <el-link href="https://github.com/maillab/cloud-mail" target="_blank" type="primary">GitHub</el-link>
            </div>
          </template>
          <el-form label-position="top">
            <el-form-item label="Cloud Mail URL">
              <el-input v-model="cloudMail.baseUrl" />
              <div class="muted mt-2">例如：https://mail.example.com，无需填写 /api。</div>
            </el-form-item>
            <el-form-item label="邮箱域名">
              <el-input v-model="cloudMail.domain" />
              <div class="muted mt-2">例如：xx.com，将生成 手机号数字@xx.com。</div>
            </el-form-item>
            <el-form-item label="Authorization Token">
              <el-input v-model="cloudMail.token" type="password" show-password />
              <div class="muted mt-2">Cloud Mail 的全局 Token，生成新 Token 会使旧 Token 失效。</div>
            </el-form-item>
            <el-divider />
            <div class="section-title">生成 Token（可选）</div>
            <el-form-item label="管理员邮箱"><el-input v-model="cloudToken.adminEmail" /></el-form-item>
            <el-form-item label="管理员密码"><el-input v-model="cloudToken.adminPassword" type="password" show-password /></el-form-item>
          </el-form>
          <div class="button-row">
            <el-button type="primary" :loading="saving.cloudMail" @click="saveCloudMail">保存配置</el-button>
            <el-button :loading="saving.cloudToken" @click="generateToken">生成并填入 Token</el-button>
          </div>
        </el-card>

        <el-card shadow="never" class="page-card">
          <template #header>AI 设置</template>
          <el-form label-position="top">
            <el-form-item label="OpenAI 自定义端点">
              <el-input v-model="ai.endpoint" />
              <div class="muted mt-2">只填写 https://xxx.com/v1，程序会自动拼接 /chat/completions。</div>
            </el-form-item>
            <el-form-item label="API Key">
              <el-input v-model="ai.apiKey" type="password" show-password />
              <div class="muted mt-2">后续任务和模块会复用这套全局 AI 配置。</div>
            </el-form-item>
            <el-form-item label="默认模型">
              <el-input v-model="ai.defaultModel" />
              <div class="muted mt-2">任务里未单独选择模型时，回退使用这里的默认模型。</div>
            </el-form-item>
            <el-form-item label="预设模型（每行一个）">
              <el-input v-model="presetModelsText" type="textarea" :rows="4" />
              <div class="muted mt-2">例如：gpt-4o-mini、qwen-vl-plus；任务编辑器会直接复用这里的预设。</div>
            </el-form-item>
            <el-form-item label="AI 失败重试次数">
              <el-input-number v-model="ai.retryCount" :min="0" :max="5" />
              <div class="muted mt-2">0 表示不重试；实际最多请求次数 = 1 + 重试次数。建议 2。</div>
            </el-form-item>
          </el-form>
          <div class="muted mb-3">测试连通会直接使用当前表单里的端点、Key、模型和重试次数，不必先保存。</div>
          <div class="button-row">
            <el-button type="primary" :loading="saving.ai" @click="saveAi">保存 AI 配置</el-button>
            <el-button :loading="saving.aiTest" @click="testAi">测试连通</el-button>
          </div>
        </el-card>

        <el-card shadow="never" class="page-card">
          <template #header>批量操作设置</template>
          <el-form label-position="top">
            <el-form-item label="默认操作间隔（毫秒）">
              <el-input-number v-model="batch.defaultDelayMs" :min="1000" :max="60000" :step="500" />
              <div class="muted mt-2">建议 2000-5000ms；最高 60000ms，用于降低批量操作频率。</div>
            </el-form-item>
            <el-form-item label="最大并发任务数">
              <el-input-number v-model="batch.maxConcurrent" :min="1" :max="10" />
              <div class="muted mt-2">同时执行的任务数量。</div>
            </el-form-item>
            <el-form-item label="历史任务保留上限">
              <el-input-number v-model="batch.historyRetentionLimit" :min="0" :max="5000" />
              <div class="muted mt-2">仅保留最新的已完成/失败任务，0 表示不限制。</div>
            </el-form-item>
            <el-checkbox v-model="batch.autoRetry">失败自动重试</el-checkbox>
            <el-form-item v-if="batch.autoRetry" label="最大重试次数" class="mt-3">
              <el-input-number v-model="batch.maxRetries" :min="1" :max="5" />
              <div class="muted mt-2">群聊活跃任务会重试连接取消、超时和失效目标；权限、Session 与风控错误不会重试。</div>
            </el-form-item>
          </el-form>
          <el-button type="primary" :loading="saving.batch" @click="saveBatch">保存配置</el-button>
        </el-card>

        <el-card shadow="never" class="page-card">
          <template #header>本地化</template>
          <el-form label-position="top">
            <el-form-item label="时区（用于面板显示和 Cron 计划）">
              <el-select v-model="timeZone.timeZoneId" class="full">
                <el-option label="北京时间（Asia/Shanghai）" value="Asia/Shanghai" />
                <el-option label="UTC" value="UTC" />
                <el-option label="北京时间（Windows: China Standard Time）" value="China Standard Time" />
              </el-select>
              <div class="muted mt-2">当前生效：{{ timeZone.effectiveHint || '-' }}</div>
            </el-form-item>
          </el-form>
          <el-alert type="info" :closable="false" show-icon class="mb-3">
            <template #title>本项目数据库中的时间统一按 UTC 存储；该设置会影响面板时间显示和 Cron 计划任务的解析时区。</template>
          </el-alert>
          <el-button type="primary" :loading="saving.timeZone" @click="saveTimeZone">保存时区</el-button>
        </el-card>
      </div>

      <div class="settings-column">
        <el-card shadow="never" class="page-card">
          <template #header>数据同步</template>
          <el-alert type="info" :closable="false" show-icon class="mb-3">
            <template #title>同步说明</template>
            <div>将每个活跃账号“当前可见的频道 + 当前可见的群组”拉取并写入本地数据库，用于频道/群组列表展示，以及批量任务自动选择执行账号。</div>
            <div>自动同步与“立即同步”都会记录到任务中心，可查看是否正在运行、已同步到第几个账号以及失败数量。</div>
            <div>同步过程中若遇到连接异常，会顺带执行一次轻量 Telegram 状态刷新，不做创建频道深度探测。</div>
            <div>不包含深度 Telegram 状态检测（创建频道探测）与验证码收取。</div>
          </el-alert>
          <el-form label-position="top">
            <el-checkbox v-model="sync.autoSyncEnabled">自动同步频道/群组数据（后台）</el-checkbox>
            <el-form-item v-if="sync.autoSyncEnabled" label="同步间隔（小时）" class="mt-3">
              <el-input-number v-model="sync.intervalHours" :min="1" :max="24" />
            </el-form-item>
          </el-form>
          <div class="button-row">
            <el-button type="primary" :loading="saving.sync" @click="saveSync">保存同步设置</el-button>
            <el-button type="primary" plain :loading="saving.syncNow" @click="syncNow">立即同步频道/群组</el-button>
          </div>

          <el-divider />
          <div class="section-title">Bot 频道秒级更新</div>
          <div class="muted mb-3">说明：开启后后台会持续轮询 Bot API（getUpdates）并应用 my_chat_member 更新，用于 Bot 被加入/撤权频道后自动出现在“Bot 频道”列表。</div>
          <el-form label-position="top">
            <el-checkbox v-model="botAutoSync.enabled">自动同步 Bot 频道（后台）</el-checkbox>
            <el-form-item v-if="botAutoSync.enabled" label="轮询间隔（秒）" class="mt-3">
              <el-input-number v-model="botAutoSync.intervalSeconds" :min="2" :max="60" />
              <div class="muted mt-2">建议 2-10 秒；过低可能更容易触发 Telegram 限流。</div>
            </el-form-item>
          </el-form>
          <el-button class="full" :loading="saving.botAutoSync" @click="saveBotAutoSync">保存 Bot 自动同步设置</el-button>

          <el-divider />
          <div class="section-title">账号状态自动刷新</div>
          <el-alert type="info" :closable="false" show-icon class="mb-3">
            <template #title>后台会定时轻量复查“连接失败 / 超时”等临时状态，避免账号明明可用但长期显示异常。</template>
          </el-alert>
          <el-form label-position="top">
            <el-checkbox v-model="telegramStatus.enabled">自动复查临时连接失败账号</el-checkbox>
            <el-row :gutter="12" class="mt-3">
              <el-col :xs="24" :sm="12">
                <el-form-item label="复查间隔（分钟）">
                  <el-input-number v-model="telegramStatus.intervalMinutes" :min="5" :max="1440" class="full" />
                </el-form-item>
              </el-col>
              <el-col :xs="24" :sm="12">
                <el-form-item label="每轮账号数">
                  <el-input-number v-model="telegramStatus.batchSize" :min="1" :max="50" class="full" />
                </el-form-item>
              </el-col>
              <el-col :xs="24" :sm="12">
                <el-form-item label="最小状态年龄（分钟）">
                  <el-input-number v-model="telegramStatus.minAgeMinutes" :min="1" :max="1440" class="full" />
                </el-form-item>
              </el-col>
              <el-col :xs="24" :sm="12">
                <el-form-item label="账号间延迟（毫秒）">
                  <el-input-number v-model="telegramStatus.delayMs" :min="0" :max="30000" :step="500" class="full" />
                </el-form-item>
              </el-col>
            </el-row>
          </el-form>
          <el-button class="full" :loading="saving.telegramStatus" @click="saveTelegramStatus">保存账号状态刷新设置</el-button>
        </el-card>

        <el-card shadow="never" class="page-card">
          <template #header>安全</template>
          <div class="muted mb-3">修改后台登录密码。</div>
          <el-button type="primary" plain @click="router.push('/admin/password')">修改密码</el-button>
        </el-card>

        <el-card shadow="never" class="page-card">
          <template #header>日志设置</template>
          <el-form label-position="top">
            <el-checkbox v-model="logging.enabled">启用日志输出</el-checkbox>
            <div class="muted mt-2">说明：关闭后仅输出 Error/Fatal（控制台），并停止写入日志文件。Docker 仍可能记录 stdout/stderr，建议同时在 docker-compose 配置日志滚动。</div>
            <el-form-item label="日志级别" class="mt-3">
              <el-select v-model="logging.level" class="full" :disabled="!logging.enabled">
                <el-option label="Debug" value="Debug" />
                <el-option label="Information" value="Information" />
                <el-option label="Warning" value="Warning" />
                <el-option label="Error" value="Error" />
              </el-select>
            </el-form-item>
            <el-form-item label="日志保留天数">
              <el-input-number v-model="logging.retentionDays" :disabled="!logging.enabled" :min="1" :max="90" />
            </el-form-item>
          </el-form>
          <el-button type="primary" :loading="saving.logging" @click="saveLogging">保存配置</el-button>
        </el-card>


        <el-card shadow="never" class="page-card">
          <template #header>存储桶备份</template>
          <el-alert type="warning" :closable="false" show-icon class="mb-3">
            <template #title>会打包数据库、appsettings.local.json、后台凭据和 sessions 后上传到指定 URL；URL 可包含 {date}、{timestamp}、{version}。</template>
          </el-alert>
          <el-form label-position="top">
            <el-checkbox v-model="bucketBackup.enabled">启用在线备份</el-checkbox>
            <el-form-item label="上传 URL" class="mt-3">
              <el-input v-model="bucketBackup.uploadUrl" placeholder="https://bucket.example.com/backups/tp-{timestamp}.zip 或预签名 URL" />
            </el-form-item>
            <el-form-item label="HTTP 方法">
              <el-select v-model="bucketBackup.method" class="full">
                <el-option label="PUT（S3/R2/OSS 预签名常用）" value="PUT" />
                <el-option label="POST" value="POST" />
              </el-select>
            </el-form-item>
            <el-form-item label="Authorization Header（可选）">
              <el-input v-model="bucketBackup.authorizationHeader" type="password" show-password placeholder="Bearer xxx；留空保持旧值" />
              <div class="muted mt-2">当前{{ bucketBackup.hasAuthorizationHeader ? '已保存鉴权头' : '未保存鉴权头' }}；勾选清空会删除旧值。</div>
            </el-form-item>
            <el-checkbox v-model="bucketBackup.clearAuthorizationHeader">清空已保存 Authorization Header</el-checkbox>
            <el-form-item label="超时（秒）" class="mt-3">
              <el-input-number v-model="bucketBackup.timeoutSeconds" :min="30" :max="1800" />
            </el-form-item>
          </el-form>
          <div class="button-row">
            <el-button type="primary" :loading="saving.bucketBackup" @click="saveBucketBackup">保存备份配置</el-button>
            <el-button type="success" plain :loading="saving.bucketBackupRun" @click="runBucketBackup">立即备份</el-button>
          </div>
        </el-card>


        <el-card shadow="never" class="page-card">
          <template #header>系统信息</template>
          <el-descriptions :column="1" border>
            <el-descriptions-item label="版本">{{ settings?.system.version || '-' }}</el-descriptions-item>
            <el-descriptions-item label="运行时">{{ settings?.system.runtime || '-' }}</el-descriptions-item>
            <el-descriptions-item label="数据库">{{ settings?.system.database || '-' }}</el-descriptions-item>
            <el-descriptions-item label="配置文件存在">{{ settings?.localConfigExists ? '是' : '否' }}</el-descriptions-item>
            <el-descriptions-item label="当前生效 ApiId">{{ settings?.system.effectiveApiId || '（未配置）' }}</el-descriptions-item>
          </el-descriptions>
          <el-button class="mt-3 full" type="warning" plain :loading="saving.cache" @click="clearCache">清除缓存</el-button>
        </el-card>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage, ElMessageBox } from 'element-plus'
import { panelApi } from '@/api/panel'
import type { SettingsPayload, TelegramApiSettings, TelegramApiProfile } from '@/api/types'

const router = useRouter()
const settings = ref<SettingsPayload | null>(null)
const telegram = reactive({ apiId: '', apiHash: '', profiles: [] as TelegramApiProfile[] })
const cloudMail = reactive({ baseUrl: '', domain: '', token: '' })
const cloudToken = reactive({ adminEmail: '', adminPassword: '' })
const ai = reactive({ endpoint: '', apiKey: '', defaultModel: '', presetModels: [] as string[], retryCount: 2 })
const batch = reactive({ defaultDelayMs: 2000, maxConcurrent: 1, historyRetentionLimit: 0, autoRetry: false, maxRetries: 3 })
const sync = reactive({ autoSyncEnabled: false, intervalHours: 6 })
const botAutoSync = reactive({ enabled: false, intervalSeconds: 2 })
const telegramStatus = reactive({ enabled: true, intervalMinutes: 15, batchSize: 10, minAgeMinutes: 10, delayMs: 2000 })
const logging = reactive({ enabled: false, level: 'Information', retentionDays: 30 })
const bucketBackup = reactive({ enabled: false, uploadUrl: '', method: 'PUT' as 'PUT' | 'POST', authorizationHeader: '', clearAuthorizationHeader: false, hasAuthorizationHeader: false, timeoutSeconds: 300 })
const timeZone = reactive({ timeZoneId: '', effectiveHint: '' as string | null })
const presetModelsText = computed({
  get: () => ai.presetModels.join('\n'),
  set: (value: string) => {
    ai.presetModels = value.split(/\r?\n/).map((x) => x.trim()).filter(Boolean)
  },
})
const saving = reactive({
  telegram: false,
  cloudMail: false,
  cloudToken: false,
  ai: false,
  aiTest: false,
  batch: false,
  sync: false,
  syncNow: false,
  botAutoSync: false,
  telegramStatus: false,
  timeZone: false,
  logging: false,
  bucketBackup: false,
  bucketBackupRun: false,
  cache: false,
})

function assign<T extends object>(target: T, source: Partial<T>) {
  Object.assign(target, source)
}

function normalizeTelegramSettings(source: TelegramApiSettings) {
  telegram.apiId = source.apiId || ''
  telegram.apiHash = source.apiHash || ''
  const profiles = source.profiles || []
  telegram.profiles = profiles.map((profile) => ({
    name: profile.name || '',
    apiId: profile.apiId || '',
    apiHash: profile.apiHash || '',
    enabled: profile.enabled !== false,
    weight: profile.weight || 1,
    notes: profile.notes || '',
  }))
}

async function load() {
  const data = await panelApi.settings()
  settings.value = data
  normalizeTelegramSettings(data.telegram)
  assign(cloudMail, data.cloudMail)
  assign(ai, data.ai)
  assign(batch, data.batch)
  assign(sync, data.sync)
  assign(botAutoSync, data.botAutoSync)
  assign(telegramStatus, data.telegramStatus)
  assign(logging, data.logging)
  assign(timeZone, data.timeZone)
  const backup = await panelApi.bucketBackupSettings()
  assign(bucketBackup, { ...backup, authorizationHeader: '', clearAuthorizationHeader: false })
}

async function run(key: keyof typeof saving, action: () => Promise<{ message?: string | null } | void>) {
  saving[key] = true
  try {
    const result = await action()
    if (result?.message) ElMessage.success(result.message)
    await load()
  } finally {
    saving[key] = false
  }
}

function addApiProfile() {
  telegram.profiles.push({ name: '', apiId: '', apiHash: '', enabled: true, weight: 1, notes: '' })
}

function removeApiProfile(index: number) {
  telegram.profiles.splice(index, 1)
}

function saveTelegram() {
  return run('telegram', () => panelApi.saveTelegramApiSettings({ ...telegram }))
}

function saveCloudMail() {
  return run('cloudMail', () => panelApi.saveCloudMailSettings({ ...cloudMail }))
}

async function generateToken() {
  if (!cloudMail.baseUrl.trim() || !cloudToken.adminEmail.trim() || !cloudToken.adminPassword.trim()) {
    ElMessage.warning('请填写 Cloud Mail URL、管理员邮箱和密码')
    return
  }
  saving.cloudToken = true
  try {
    const result = await panelApi.generateCloudMailToken({
      baseUrl: cloudMail.baseUrl,
      adminEmail: cloudToken.adminEmail,
      adminPassword: cloudToken.adminPassword,
    })
    cloudMail.token = result.token
    ElMessage.success('Token 已生成并填入')
  } finally {
    saving.cloudToken = false
  }
}

function saveAi() {
  return run('ai', () => panelApi.saveAiSettings({ ...ai }))
}

async function testAi() {
  saving.aiTest = true
  try {
    const result = await panelApi.testAiSettings({ ...ai })
    ElMessage.success(`AI 连通成功：${result.model || ai.defaultModel || '默认模型'}`)
  } finally {
    saving.aiTest = false
  }
}

function saveBatch() {
  return run('batch', () => panelApi.saveBatchSettings({ ...batch }))
}

function saveSync() {
  return run('sync', () => panelApi.saveSyncSettings({ ...sync }))
}

function syncNow() {
  return run('syncNow', () => panelApi.startSyncNow())
}

function saveBotAutoSync() {
  return run('botAutoSync', () => panelApi.saveBotAutoSyncSettings({ ...botAutoSync }))
}

function saveTelegramStatus() {
  return run('telegramStatus', () => panelApi.saveTelegramStatusSettings({ ...telegramStatus }))
}

function saveTimeZone() {
  return run('timeZone', () => panelApi.saveTimeZoneSettings({ ...timeZone }))
}

function saveLogging() {
  return run('logging', () => panelApi.saveLoggingSettings({ ...logging }))
}

function saveBucketBackup() {
  return run('bucketBackup', () => panelApi.saveBucketBackupSettings({
    enabled: bucketBackup.enabled,
    uploadUrl: bucketBackup.uploadUrl,
    method: bucketBackup.method,
    authorizationHeader: bucketBackup.authorizationHeader,
    clearAuthorizationHeader: bucketBackup.clearAuthorizationHeader,
    timeoutSeconds: bucketBackup.timeoutSeconds,
  }))
}

async function runBucketBackup() {
  await ElMessageBox.confirm('将立即打包并上传数据库、凭据和 Session，确认继续？', '立即存储桶备份', { type: 'warning' })
  await run('bucketBackupRun', async () => {
    const result = await panelApi.runBucketBackup()
    return { message: result.message }
  })
}

async function clearCache() {
  await ElMessageBox.confirm('确定要清除 Telegram 客户端缓存吗？', '确认清除', { type: 'warning' })
  await run('cache', () => panelApi.clearCache())
}

onMounted(load)
</script>

<style scoped>
.settings-columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.settings-column {
  display: grid;
  gap: 16px;
  align-content: start;
}

.button-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.full {
  width: 100%;
}

.mb-4 {
  margin-bottom: 16px;
}

.mt-2 {
  margin-top: 8px;
}

.mt-3 {
  margin-top: 12px;
}

.section-title {
  font-weight: 600;
  margin-bottom: 8px;
}

.api-profile-row {
  border: 1px solid var(--el-border-color-lighter);
  border-radius: 8px;
  padding: 12px;
  margin-bottom: 12px;
}

@media (max-width: 960px) {
  .settings-columns {
    grid-template-columns: 1fr;
  }
}
</style>
