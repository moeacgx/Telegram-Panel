<template>
  <div class="account-login-page">
    <el-card shadow="never" class="login-flow-card">
      <template #header>
        <div class="card-header">
          <span>手动登录</span>
          <el-tag v-if="activeLoginId > 0" type="info" effect="plain">会话 {{ activeLoginId }}</el-tag>
        </div>
      </template>

      <div class="mode-row">
        <el-segmented
          v-model="loginMode"
          :options="modeOptions"
          :disabled="logging || hasActiveLoginSession"
        />
      </div>

      <section class="login-proxy-route" aria-label="手动登录首次连接出口">
        <div class="login-proxy-heading">
          <span class="material-icons">vpn_lock</span>
          <div>
            <div class="cell-main">手动登录首次连接出口</div>
            <div class="cell-sub">必须先选择；发送验证码或生成二维码之前生效，登录期间不可切换</div>
          </div>
        </div>
        <el-radio-group v-model="proxyStrategy" class="login-proxy-options" :disabled="proxyRouteLocked">
          <el-radio-button value="existing">已有代理</el-radio-button>
          <el-radio-button value="warp_pool" :disabled="availableWarpPoolCount === 0">自动分配已有 WARP</el-radio-button>
          <el-radio-button value="global">全局设置</el-radio-button>
          <el-radio-button value="direct">直连（确认风险）</el-radio-button>
        </el-radio-group>
        <el-select
          v-if="proxyStrategy === 'existing'"
          v-model="proxyId"
          class="login-proxy-select"
          filterable
          placeholder="选择首次登录使用的代理"
          :disabled="proxyRouteLocked"
        >
          <el-option
            v-for="proxy in proxies"
            :key="proxy.id"
            :value="proxy.id"
            :label="`${proxy.name} · ${proxy.protocol.toUpperCase()} · ${proxy.egressIp || `${proxy.host}:${proxy.port}`}`"
            :disabled="!proxy.isEnabled"
          />
        </el-select>
        <div v-if="!proxyStrategy" class="login-proxy-notice warning">
          为防止首个 Telegram 请求使用面板直连 IP，请明确选择已有代理或自动分配已有 WARP。
        </div>
        <div v-else-if="proxyStrategy === 'direct'" class="login-proxy-notice danger">
          已明确选择直连：Telegram 从发送验证码或生成二维码开始即可看到面板公网 IP。
        </div>
        <div v-else-if="proxyStrategy === 'global'" class="login-proxy-notice warning">
          仅在已配置全局代理时可用；未配置会在首次连接前拒绝，请改选已有代理、自动分配已有 WARP 或明确直连。
        </div>
        <div v-else-if="proxyStrategy === 'warp_pool'" class="login-proxy-notice warning">
          将按当前账号绑定数自动选择已有 WARP；不会创建新容器。当前已启用 {{ availableWarpPoolCount }} 个 WARP。
        </div>
      </section>

      <section class="login-device-profile" aria-label="手动登录设备指纹">
        <div class="login-proxy-heading">
          <span class="material-icons">fingerprint</span>
          <div>
            <div class="cell-main">本次登录设备指纹</div>
            <div class="cell-sub">发送验证码或生成二维码前生效；成功后保存到账号，类似 TokensPro 的设备画像选择</div>
          </div>
        </div>
        <el-select v-model="deviceProfileKey" class="login-device-select" filterable :disabled="proxyRouteLocked">
          <el-option label="随机设备指纹" value="random" />
          <el-option
            v-for="profile in deviceProfiles"
            :key="profile.key"
            :value="profile.key"
            :label="`${profile.name} · ${profile.family}`"
          />
        </el-select>
        <div v-if="randomDeviceProfileSelected" class="login-device-summary">
          <span class="material-icons">casino</span>
          <span>按账号/会话稳定随机选择适合当前 API 的内置画像</span>
        </div>
        <div v-else-if="selectedDeviceProfile" class="login-device-summary">
          <span class="material-icons">devices</span>
          <span>{{ selectedDeviceProfile.deviceModel }} · {{ selectedDeviceProfile.systemVersion }} · App {{ selectedDeviceProfile.appVersion }}</span>
        </div>
      </section>

      <template v-if="loginMode === 'qr'">
        <div class="qr-body">
          <el-alert
            v-if="telegramApiChecked && !telegramApiConfigured"
            type="error"
            :closable="false"
            show-icon
            class="mb-3"
          >
            <template #title>Telegram API 当前不可用，无法登录。</template>
            <div class="login-api-warning">
              <span>当前生效 ApiId：{{ effectiveApiId || '未配置' }}</span>
              <el-button size="small" type="primary" @click="router.push('/settings')">去系统设置</el-button>
            </div>
          </el-alert>

          <el-result v-if="qrStatus === 'authorized'" icon="success" title="登录成功并保存到数据库">
            <template #sub-title>
              <div v-if="savedAccount" class="success-lines">
                <div>编号：#{{ savedAccount.displayNumber }}</div>

                <div>手机号：{{ savedAccount.displayPhone }}</div>
                <div>用户名：{{ savedAccount.username ? `@${savedAccount.username}` : '无' }}</div>
                <div>用户ID：{{ savedAccount.userId }}</div>
              </div>
            </template>
            <template #extra>
              <el-button type="primary" @click="router.push('/accounts')">查看账号列表</el-button>
              <el-button @click="reset">继续登录下一个账号</el-button>
            </template>
          </el-result>

          <div v-else class="qr-layout">
            <div class="qr-box">
              <img v-if="qrDataUrl" :src="qrDataUrl" alt="Telegram 扫码登录二维码" />
              <div v-else class="qr-placeholder">
                <span class="material-icons">qr_code_2</span>
                <span>{{ qrStatusText }}</span>
              </div>
            </div>

            <div class="qr-side">
              <div class="qr-title">使用 Telegram 扫码登录</div>
              <div class="qr-desc">打开 Telegram，进入设置里的设备管理，扫描此二维码并确认登录。</div>
              <el-tag :type="qrTagType" effect="plain">{{ qrStatusText }}</el-tag>
              <div v-if="qrExpiresText" class="qr-expires">过期时间：{{ qrExpiresText }}</div>

              <el-form
                v-if="qrStatus === 'password'"
                label-position="top"
                class="qr-password-form"
                autocomplete="off"
                data-lpignore="true"
                data-1p-ignore="true"
                data-bwignore="true"
              >
                <div class="autofill-decoy" aria-hidden="true">
                  <input tabindex="-1" autocomplete="username" name="username" type="text" />
                  <input tabindex="-1" autocomplete="current-password" name="password" type="password" />
                </div>
                <el-form-item label="系统账号已保存二级密码">
                  <el-select
                    v-model="qrStoredAccountId"
                    class="full"
                    filterable
                    clearable
                    :loading="storedAccountsLoading"
                    placeholder="可选：选择系统中已有账号"
                    @visible-change="onStoredAccountSelectVisible"
                    @change="onQrStoredAccountChanged"
                  >
                    <el-option
                      v-for="account in storedAccounts"
                      :key="account.id"
                      :label="accountLabel(account)"
                      :value="account.id"
                    />
                  </el-select>
                  <div class="muted mt-2">扫码登录无法提前识别手机号，选择对应账号后会带入系统已保存的二级密码。</div>
                </el-form-item>
                <el-alert
                  v-if="qrPasswordSource"
                  :title="qrPasswordSource"
                  type="success"
                  :closable="false"
                  show-icon
                  class="mb-3"
                />
                <el-form-item label="两步验证密码">
                  <el-input
                    v-model="qrPassword"
                    type="password"
                    show-password
                    name="telegram-qr-2fa"
                    autocomplete="new-password"
                    data-lpignore="true"
                    data-1p-ignore="true"
                    data-bwignore="true"
                    data-form-type="other"
                    @keyup.enter="submitQrPassword"
                  />
                </el-form-item>
                <el-checkbox v-model="saveQrPasswordToSystem">登录成功后保存到系统</el-checkbox>
                <div class="muted">默认不保存。重要账号建议关闭，避免二级密码长期落库。</div>
                <el-button type="primary" :loading="logging" @click="submitQrPassword">验证密码</el-button>
              </el-form>
            </div>
          </div>
        </div>
      </template>

      <template v-else>
        <el-steps :active="stepIndex" finish-status="success" class="steps">
          <el-step title="手机号" />
          <el-step title="验证码" />
          <el-step title="二级密码" />
          <el-step title="完成" />
        </el-steps>

        <div v-if="currentStep === 'phone'" class="step-body">
          <el-alert
            v-if="telegramApiChecked && !telegramApiConfigured"
            type="error"
            :closable="false"
            show-icon
            class="mb-3"
          >
            <template #title>Telegram API 当前不可用，无法登录。</template>
            <div class="login-api-warning">
              <span>当前生效 ApiId：{{ effectiveApiId || '未配置' }}</span>
              <el-button size="small" type="primary" @click="router.push('/settings')">去系统设置</el-button>
            </div>
          </el-alert>
          <el-alert
            title="手机号需包含国家代码，例如 +8613800138000。系统会使用系统设置里的 Telegram API 池发送验证码。"
            type="info"
            :closable="false"
            show-icon
          />
          <el-form label-position="top" class="mt-4">
            <el-form-item label="手机号">
              <el-input v-model="phone" placeholder="+86xxxxxxxxxx" @keyup.enter="next" />
            </el-form-item>
          </el-form>
        </div>

        <div v-else-if="currentStep === 'code'" class="step-body">
          <el-alert
            :title="`验证码已发送到 ${phone}`"
            description="验证码通常优先发送到已登录设备的 Telegram 系统通知（777000）；没有可用设备时才可能发送短信或电话。"
            type="info"
            :closable="false"
            show-icon
          />
          <el-form label-position="top" class="mt-4">
            <el-form-item label="验证码">
              <el-input v-model="code" placeholder="12345" @keyup.enter="next" />
            </el-form-item>
          </el-form>
          <el-button :loading="logging" :disabled="loginId === 0" @click="resendCode">重新发送验证码（尝试切换通道）</el-button>
        </div>

        <div v-else-if="currentStep === 'password'" class="step-body">
          <el-alert :title="phonePasswordAlertTitle" :type="phonePasswordSource ? 'success' : 'info'" :closable="false" show-icon />
          <el-form
            label-position="top"
            class="mt-4"
            autocomplete="off"
            data-lpignore="true"
            data-1p-ignore="true"
            data-bwignore="true"
          >
            <div class="autofill-decoy" aria-hidden="true">
              <input tabindex="-1" autocomplete="username" name="username" type="text" />
              <input tabindex="-1" autocomplete="current-password" name="password" type="password" />
            </div>
            <el-form-item label="两步验证密码">
              <el-input
                v-model="password"
                type="password"
                show-password
                name="telegram-phone-2fa"
                autocomplete="new-password"
                data-lpignore="true"
                data-1p-ignore="true"
                data-bwignore="true"
                data-form-type="other"
                :placeholder="phonePasswordLoading ? '正在查询系统已保存的二级密码' : '请输入两步验证密码'"
                @keyup.enter="next"
              />
              <div class="muted mt-2">如果该手机号已存在于系统并保存过二级密码，会自动带入；也可以手动修改。</div>
            </el-form-item>
            <el-form-item>
              <div>
                <el-checkbox v-model="savePhonePasswordToSystem">登录成功后保存到系统</el-checkbox>
                <div class="muted">默认不保存。重要账号建议关闭，避免二级密码长期落库。</div>
              </div>
            </el-form-item>
          </el-form>
        </div>

        <div v-else class="step-body">
          <el-result icon="success" title="登录成功并保存到数据库">
            <template #sub-title>
              <div v-if="savedAccount" class="success-lines">
                <div>编号：#{{ savedAccount.displayNumber }}</div>

                <div>手机号：{{ savedAccount.displayPhone }}</div>
                <div>用户名：{{ savedAccount.username ? `@${savedAccount.username}` : '无' }}</div>
                <div>用户ID：{{ savedAccount.userId }}</div>
              </div>
            </template>
            <template #extra>
              <el-button type="primary" @click="router.push('/accounts')">查看账号列表</el-button>
              <el-button @click="reset">继续登录下一个账号</el-button>
            </template>
          </el-result>
        </div>
      </template>

      <template #footer>
        <div class="footer-actions">
          <el-button :disabled="logging" @click="reset">重置</el-button>
          <div class="spacer" />

          <template v-if="loginMode === 'qr'">
            <el-button
              type="primary"
              :loading="logging"
              :disabled="(telegramApiChecked && !telegramApiConfigured) || proxySelectionInvalid"
              @click="startQrLogin"
            >
              {{ qrLoginId > 0 ? '重新生成二维码' : '生成二维码' }}
            </el-button>
          </template>

          <template v-else>
            <el-button v-if="currentStep === 'code' || currentStep === 'password'" :disabled="logging" @click="previous">上一步</el-button>
            <el-button
              v-if="currentStep !== 'done'"
              type="primary"
              :loading="logging"
              :disabled="currentStep === 'phone' && ((telegramApiChecked && !telegramApiConfigured) || proxySelectionInvalid)"
              @click="next"
            >
              {{ primaryButtonText }}
            </el-button>
          </template>
        </div>
      </template>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import QRCode from 'qrcode'
import { panelApi } from '@/api/panel'
import type {
  AccountDetail,
  AccountListItem,
  AccountLoginResponse,
  AccountProxyStrategy,
  AccountQrLoginResponse,
  OutboundProxy,
  TelegramDeviceProfile,
} from '@/api/types'
type LoginStep = 'phone' | 'code' | 'password' | 'done'
type LoginMode = 'qr' | 'phone'

const router = useRouter()
const logging = ref(false)
const phone = ref('')
const code = ref('')
const password = ref('')
const loginId = ref(0)
const qrLoginId = ref(0)
const currentStep = ref<LoginStep>('phone')
const loginMode = ref<LoginMode>('qr')
const savedAccount = ref<AccountListItem | null>(null)
const telegramApiChecked = ref(false)
const telegramApiConfigured = ref(true)
const effectiveApiId = ref('')
const qrDataUrl = ref('')
const qrStatus = ref('idle')
const qrMessage = ref('')
const qrExpiresAt = ref<string | null>(null)
const qrPassword = ref('')
const qrPolling = ref(false)
const storedAccounts = ref<AccountListItem[]>([])
const storedAccountsLoading = ref(false)
const storedAccountsLoaded = ref(false)
const qrStoredAccountId = ref<number | null>(null)
const qrPasswordSource = ref('')
const phonePasswordLoading = ref(false)
const phonePasswordSource = ref('')
const savePhonePasswordToSystem = ref(false)
const saveQrPasswordToSystem = ref(false)
const proxies = ref<OutboundProxy[]>([])
const proxyStrategy = ref<AccountProxyStrategy | ''>('')
const proxyId = ref<number | null>(null)
const deviceProfiles = ref<TelegramDeviceProfile[]>([])
const deviceProfileKey = ref('')
let qrTimer: number | undefined

const modeOptions = [
  { label: '扫码登录', value: 'qr' },
  { label: '手机号登录', value: 'phone' },
]

const activeLoginId = computed(() => (loginMode.value === 'qr' ? qrLoginId.value : loginId.value))
const hasActiveLoginSession = computed(() => loginId.value > 0 || qrLoginId.value > 0)
const availableWarpPoolCount = computed(() => proxies.value.filter(
  (proxy) => proxy.kind === 'warp'
    && proxy.isEnabled
    && proxy.warpRuntimeStatus === 'active',
).length)
const proxySelectionInvalid = computed(() =>
  !proxyStrategy.value
  || (proxyStrategy.value === 'existing' && !proxyId.value)
  || (proxyStrategy.value === 'warp_pool' && availableWarpPoolCount.value === 0),
)
const proxyRouteLocked = computed(() => logging.value || hasActiveLoginSession.value)
const randomDeviceProfileSelected = computed(() => deviceProfileKey.value === 'random')
const selectedDeviceProfile = computed(() => deviceProfiles.value.find((profile) => profile.key === deviceProfileKey.value))

const stepIndex = computed(() => {
  if (currentStep.value === 'phone') return 0
  if (currentStep.value === 'code') return 1
  if (currentStep.value === 'password') return 2
  return 4
})

const primaryButtonText = computed(() => {
  if (currentStep.value === 'phone') return '发送验证码'
  if (currentStep.value === 'code') return '验证验证码'
  if (currentStep.value === 'password') return '验证密码'
  return '下一步'
})

const qrStatusText = computed(() => {
  if (qrMessage.value) return qrMessage.value
  if (qrStatus.value === 'idle') return '点击生成二维码'
  if (qrStatus.value === 'pending') return '等待扫码确认'
  if (qrStatus.value === 'password') return '需要两步验证密码'
  if (qrStatus.value === 'expired') return '二维码已过期'
  if (qrStatus.value === 'failed') return '扫码登录失败'
  return '等待扫码确认'
})

const qrTagType = computed(() => {
  if (qrStatus.value === 'failed' || qrStatus.value === 'expired') return 'danger'
  if (qrStatus.value === 'password') return 'warning'
  if (qrStatus.value === 'authorized') return 'success'
  return 'info'
})

const qrExpiresText = computed(() => {
  if (!qrExpiresAt.value) return ''
  const date = new Date(qrExpiresAt.value)
  if (Number.isNaN(date.getTime())) return ''
  return date.toLocaleTimeString()
})

const phonePasswordAlertTitle = computed(() => {
  if (phonePasswordSource.value) return phonePasswordSource.value
  if (phonePasswordLoading.value) return '正在查询系统中是否保存了该账号的二级密码'
  return '此账号启用了两步验证，请输入密码。'
})

function ensureProxySelected() {
  if (!proxySelectionInvalid.value) return true
  if (!proxyStrategy.value) {
    ElMessage.warning('请先明确选择本次登录首次连接使用的代理方式')
  } else {
    ElMessage.warning(proxyStrategy.value === 'warp_pool' ? '当前没有可自动分配的已有 WARP' : '请选择已有代理')
  }
  return false
}

function selectedProxyPayload(): { proxyStrategy: AccountProxyStrategy; proxyId: number | null } {
  const strategy = proxyStrategy.value as AccountProxyStrategy
  return {
    proxyStrategy: strategy,
    proxyId: strategy === 'existing' ? proxyId.value : null,
  }
}

async function loadLoginProxyOptions() {
  proxies.value = await panelApi.proxies()
  if (availableWarpPoolCount.value === 0 && proxyStrategy.value === 'warp_pool') {
    proxyStrategy.value = ''
    proxyId.value = null
  }
}

async function next() {
  if (logging.value) return
  if (currentStep.value === 'phone' && telegramApiChecked.value && !telegramApiConfigured.value) {
    ElMessage.warning('请先检查 Telegram API 配置')
    return
  }
  if (currentStep.value === 'phone' && !ensureProxySelected()) return

  logging.value = true
  try {
    if (currentStep.value === 'phone') {
      if (!phone.value.trim()) {
        ElMessage.warning('请输入手机号（包含国家代码）')
        return
      }
      const response = await panelApi.startAccountLogin({
        phone: phone.value,
        loginId: loginId.value,
        deviceProfileKey: deviceProfileKey.value || null,
        ...selectedProxyPayload(),
      })
      await handleLoginResponse(response)
      return
    }

    if (currentStep.value === 'code') {
      if (loginId.value <= 0) {
        ElMessage.warning('请先发送验证码')
        currentStep.value = 'phone'
        return
      }
      if (!code.value.trim()) {
        ElMessage.warning('请输入验证码')
        return
      }
      const response = await panelApi.submitAccountLoginCode(loginId.value, code.value)
      await handleLoginResponse(response)
      return
    }

    if (currentStep.value === 'password') {
      if (loginId.value <= 0) {
        ElMessage.warning('请先发送验证码')
        currentStep.value = 'phone'
        return
      }
      if (!password.value.trim()) {
        ElMessage.warning('请输入两步验证密码')
        return
      }
      const response = await panelApi.submitAccountLoginPassword(loginId.value, password.value, savePhonePasswordToSystem.value)
      await handleLoginResponse(response)
    }
  } finally {
    logging.value = false
  }
}

async function loadTelegramApiStatus() {
  try {
    const settings = await panelApi.settings()
    deviceProfiles.value = settings.telegram.deviceProfiles || []
    if (!deviceProfileKey.value) {
      deviceProfileKey.value = settings.telegram.defaultDeviceProfileKey || deviceProfiles.value[0]?.key || ''
    }
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

async function startQrLogin() {
  if (logging.value) return
  if (telegramApiChecked.value && !telegramApiConfigured.value) {
    ElMessage.warning('请先检查 Telegram API 配置')
    return
  }
  if (!ensureProxySelected()) return

  logging.value = true
  try {
    stopQrPolling()
    // 重新生成二维码必须携带原登录 ID，让服务端复用已经冻结的代理状态。
    // 若先调用取消接口，独立 WARP/Resin 身份会被释放并重建，导致登录中途更换出口。
    const existingLoginId = qrLoginId.value
    qrDataUrl.value = ''
    qrPassword.value = ''
    qrPasswordSource.value = ''
    qrStoredAccountId.value = null
    saveQrPasswordToSystem.value = false
    qrStatus.value = 'pending'
    qrMessage.value = '正在生成二维码'

    const response = await panelApi.startAccountQrLogin({
      loginId: existingLoginId || undefined,
      deviceProfileKey: deviceProfileKey.value || null,
      ...selectedProxyPayload(),
    })
    await handleQrResponse(response)
    if (response.status === 'pending') startQrPolling()
  } finally {
    logging.value = false
  }
}

async function pollQrLogin() {
  if (qrLoginId.value <= 0) return
  try {
    const response = await panelApi.pollAccountQrLogin(qrLoginId.value)
    await handleQrResponse(response)
    if (['authorized', 'failed', 'expired', 'password'].includes(response.status)) {
      stopQrPolling()
    }
  } catch {
    stopQrPolling()
  }
}

function startQrPolling() {
  stopQrPolling()
  qrPolling.value = true
  qrTimer = window.setInterval(() => {
    void pollQrLogin()
  }, 2500)
}

function stopQrPolling() {
  if (qrTimer) {
    window.clearInterval(qrTimer)
    qrTimer = undefined
  }
  qrPolling.value = false
}

async function submitQrPassword() {
  if (qrLoginId.value <= 0) {
    ElMessage.warning('扫码登录会话已失效，请重新生成二维码')
    return
  }
  if (!qrPassword.value.trim()) {
    ElMessage.warning('请输入两步验证密码')
    return
  }

  logging.value = true
  try {
    const response = await panelApi.submitAccountQrLoginPassword(qrLoginId.value, qrPassword.value, saveQrPasswordToSystem.value)
    await handleQrResponse(response)
  } finally {
    logging.value = false
  }
}

function normalizePhoneDigits(value?: string | null) {
  let digits = String(value || '').replace(/\D/g, '')
  if (digits.startsWith('00')) digits = digits.slice(2)
  return digits
}

function accountLabel(account: AccountListItem | AccountDetail) {
  const nickname = account.nickname ? ` / ${account.nickname}` : ''
  const username = account.username ? ` / @${account.username}` : ''
  return `#${account.displayNumber} ${account.displayPhone}${nickname}${username}`
}

async function ensureStoredAccountsLoaded() {
  if (storedAccountsLoaded.value || storedAccountsLoading.value) return
  storedAccountsLoading.value = true
  try {
    const result = await panelApi.accounts({ page: 1, pageSize: 500 })
    storedAccounts.value = result.items
    storedAccountsLoaded.value = true
  } finally {
    storedAccountsLoading.value = false
  }
}

function onStoredAccountSelectVisible(visible: boolean) {
  if (visible) void ensureStoredAccountsLoaded()
}

async function onQrStoredAccountChanged(value: number | null) {
  qrStoredAccountId.value = value || null
  qrPasswordSource.value = ''
  if (!qrStoredAccountId.value) return

  try {
    const account = await panelApi.account(qrStoredAccountId.value)
    if (!account.twoFactorPassword?.trim()) {
      ElMessage.warning('该账号没有保存二级密码')
      return
    }

    qrPassword.value = account.twoFactorPassword
    qrPasswordSource.value = `已从系统账号 ${account.displayPhone} 带入已保存的二级密码`
  } catch {
    ElMessage.warning('读取账号已保存二级密码失败')
  }
}

async function prefillPhonePasswordFromStoredAccount() {
  phonePasswordSource.value = ''
  const phoneDigits = normalizePhoneDigits(phone.value)
  if (!phoneDigits) return

  phonePasswordLoading.value = true
  try {
    const result = await panelApi.accounts({ page: 1, pageSize: 20, search: phoneDigits })
    const matched = result.items.find((account) => normalizePhoneDigits(account.displayPhone) === phoneDigits)
      || (result.items.length === 1 ? result.items[0] : null)
    if (!matched) return

    const account = await panelApi.account(matched.id)
    if (!account.twoFactorPassword?.trim()) return

    password.value = account.twoFactorPassword
    phonePasswordSource.value = `已从系统账号 ${account.displayPhone} 带入已保存的二级密码`
  } catch {
    // 预填失败不影响正常手动输入
  } finally {
    phonePasswordLoading.value = false
  }
}

async function handleQrResponse(response: AccountQrLoginResponse) {
  qrLoginId.value = response.loginId
  qrStatus.value = response.status
  qrMessage.value = response.message || ''
  qrExpiresAt.value = response.expiresAtUtc || null

  if (response.qrLoginUrl) {
    qrDataUrl.value = await QRCode.toDataURL(response.qrLoginUrl, {
      width: 248,
      margin: 1,
      color: { dark: '#111827', light: '#ffffff' },
    })
  }

  if (response.success) {
    stopQrPolling()
    savedAccount.value = response.account || null
    qrStatus.value = 'authorized'
    qrMessage.value = response.message || '扫码登录成功'
    qrLoginId.value = 0
    qrDataUrl.value = ''
    ElMessage.success(response.message || '扫码登录成功')
    return
  }

  if (response.status === 'password') {
    void ensureStoredAccountsLoaded()
    ElMessage.info(response.message || '请输入两步验证密码')
    return
  }

  if (response.status === 'pending' && qrLoginId.value > 0 && !qrPolling.value) {
    startQrPolling()
    return
  }

  if (response.status === 'failed' || response.status === 'expired') {
    ElMessage.error(response.message || '扫码登录失败')
  }
}

async function resendCode() {
  if (loginId.value <= 0) {
    ElMessage.warning('请先在上一步发送验证码')
    return
  }

  logging.value = true
  try {
    const response = await panelApi.resendAccountLoginCode(loginId.value)
    await handleLoginResponse(response, false)
    ElMessage.info(response.message || '已请求重新发送验证码')
  } finally {
    logging.value = false
  }
}

async function handleLoginResponse(response: AccountLoginResponse, showMessage = true) {
  loginId.value = response.loginId

  if (response.success) {
    savedAccount.value = response.account || null
    currentStep.value = 'done'
    if (showMessage) ElMessage.success(response.message || '登录成功')
    loginId.value = 0
    return
  }

  if (response.nextStep === 'code') {
    currentStep.value = 'code'
    if (showMessage) ElMessage.info(response.message || '请输入验证码')
    return
  }

  if (response.nextStep === 'password') {
    currentStep.value = 'password'
    await prefillPhonePasswordFromStoredAccount()
    if (showMessage) ElMessage.info(response.message || '请输入两步验证密码')
    return
  }

  if (response.nextStep === 'signup') {
    ElMessage.error(response.message || '该手机号需要注册新账号，当前面板不处理注册流程')
    return
  }

  if (response.nextStep === 'email' || response.nextStep === 'email_code') {
    ElMessage.error(response.message || '该账号需要邮箱验证，请使用已保存会话或在 Telegram 客户端完成验证后再导入')
    return
  }

  ElMessage.error(response.message || '登录失败')
}

async function previous() {
  if (currentStep.value === 'password') {
    currentStep.value = 'code'
    return
  }
  if (currentStep.value !== 'code' || logging.value) return

  // 返回手机号步骤等同于放弃当前授权会话。必须先释放冻结路由，
  // 否则再次发送验证码会重新创建 WARP/Resin 身份，导致同一登录流程切换出口。
  logging.value = true
  try {
    if (loginId.value > 0) {
      await panelApi.resetAccountLogin(loginId.value)
    }
    loginId.value = 0
    code.value = ''
    password.value = ''
    proxyStrategy.value = ''
    proxyId.value = null
    currentStep.value = 'phone'
  } catch {
    ElMessage.error('旧登录会话无法安全释放，已阻止返回手机号步骤，请重试或重置登录')
  } finally {
    logging.value = false
  }
}

async function cleanupCurrentSession() {
  stopQrPolling()
  if (loginId.value > 0) {
    await panelApi.resetAccountLogin(loginId.value)
    loginId.value = 0
  }
  if (qrLoginId.value > 0) {
    await panelApi.cancelAccountQrLogin(qrLoginId.value)
    qrLoginId.value = 0
  }
}

async function reset() {
  if (logging.value) return
  logging.value = true
  try {
    await cleanupCurrentSession()
    proxyStrategy.value = ''
    proxyId.value = null
    phone.value = ''
    code.value = ''
    password.value = ''
    qrPassword.value = ''
    qrPasswordSource.value = ''
    qrStoredAccountId.value = null
    phonePasswordSource.value = ''
    phonePasswordLoading.value = false
    savePhonePasswordToSystem.value = false
    saveQrPasswordToSystem.value = false
    qrDataUrl.value = ''
    qrStatus.value = 'idle'
    qrMessage.value = ''
    qrExpiresAt.value = null
    savedAccount.value = null
    currentStep.value = 'phone'
  } catch {
    return
  } finally {
    logging.value = false
  }
}

watch(loginMode, async (_value, oldValue) => {
  if (!oldValue) return
  logging.value = true
  try {
    await cleanupCurrentSession()
    proxyStrategy.value = ''
    proxyId.value = null
    phone.value = ''
    code.value = ''
    password.value = ''
    qrPassword.value = ''
    qrPasswordSource.value = ''
    qrStoredAccountId.value = null
    phonePasswordSource.value = ''
    phonePasswordLoading.value = false
    savePhonePasswordToSystem.value = false
    saveQrPasswordToSystem.value = false
    qrDataUrl.value = ''
    qrStatus.value = 'idle'
    qrMessage.value = ''
    qrExpiresAt.value = null
    savedAccount.value = null
    currentStep.value = 'phone'
  } catch {
    return
  } finally {
    logging.value = false
  }
})

onBeforeUnmount(() => {
  stopQrPolling()
  if (loginId.value > 0) {
    panelApi.resetAccountLogin(loginId.value).catch(() => {})
  }
  if (qrLoginId.value > 0) {
    panelApi.cancelAccountQrLogin(qrLoginId.value).catch(() => {})
  }
})

onMounted(async () => {
  await Promise.allSettled([
    loadTelegramApiStatus(),
    loadLoginProxyOptions(),
  ])
})
</script>

<style scoped>
.account-login-page {
  min-height: calc(100vh - 96px);
  display: flex;
  justify-content: center;
  align-items: flex-start;
}

.autofill-decoy {
  position: fixed;
  top: -10000px;
  left: -10000px;
  width: 1px;
  height: 1px;
  overflow: hidden;
  opacity: 0;
  pointer-events: none;
}

.autofill-decoy input {
  width: 1px;
  height: 1px;
  border: 0;
  padding: 0;
}

.login-flow-card {
  width: min(760px, 100%);
  border-color: var(--tp-border);
}

.mode-row {
  display: flex;
  justify-content: center;
  margin-bottom: 22px;
}

.mode-row :deep(.el-segmented) {
  min-width: 260px;
}

.login-proxy-route,
.login-device-profile {
  display: grid;
  gap: 12px;
  margin-bottom: 22px;
  padding: 14px 16px;
  border: 1px solid var(--tp-border);
  border-left: 4px solid var(--el-color-primary);
  border-radius: 4px;
  background: var(--tp-panel);
}

.login-proxy-heading,
.login-device-summary {
  display: flex;
  align-items: center;
  gap: 10px;
}

.login-proxy-heading .material-icons {
  flex: 0 0 auto;
  color: var(--el-color-primary);
  font-size: 26px;
}

.login-proxy-options {
  display: flex;
  flex-wrap: wrap;
}

.login-proxy-select,
.login-device-select {
  width: 100%;
}

.login-device-summary {
  color: var(--tp-muted);
  font-size: 13px;
  line-height: 1.6;
}

.login-proxy-notice {
  font-size: 13px;
  line-height: 1.6;
}

.login-proxy-notice.warning {
  color: var(--el-color-warning-dark-2);
}

.login-proxy-notice.danger {
  color: var(--el-color-danger);
}

.login-proxy-notice.success {
  color: var(--el-color-success);
}

.steps {
  margin-bottom: 24px;
}

.step-body,
.qr-body {
  min-height: 260px;
}

.qr-layout {
  display: grid;
  grid-template-columns: 280px minmax(0, 1fr);
  gap: 24px;
  align-items: center;
}

.qr-box {
  width: 280px;
  height: 280px;
  display: flex;
  align-items: center;
  justify-content: center;
  border: 1px solid var(--tp-border);
  background: #fff;
}

.qr-box img {
  width: 248px;
  height: 248px;
  display: block;
}

.qr-placeholder {
  display: grid;
  justify-items: center;
  gap: 10px;
  color: #697386;
  font-size: 13px;
}

.qr-placeholder .material-icons {
  font-size: 58px;
  color: #1976d2;
}

.qr-side {
  display: grid;
  align-content: center;
  gap: 12px;
}

.qr-title {
  font-size: 18px;
  font-weight: 650;
}

.qr-desc,
.qr-expires {
  color: var(--tp-muted);
  line-height: 1.7;
}

.qr-password-form {
  margin-top: 6px;
}

.footer-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.spacer {
  flex: 1;
}

.success-lines {
  display: grid;
  gap: 4px;
  color: var(--tp-muted);
}

.login-api-warning {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
  margin-top: 6px;
}

@media (max-width: 760px) {
  .login-proxy-options {
    display: grid;
    grid-template-columns: repeat(2, minmax(0, 1fr));
    width: 100%;
  }

  .login-proxy-options :deep(.el-radio-button),
  .login-proxy-options :deep(.el-radio-button__inner) {
    width: 100%;
    min-width: 0;
  }

  .login-proxy-options :deep(.el-radio-button__inner) {
    height: 100%;
    padding: 8px 10px;
    white-space: normal;
  }

  .qr-layout {
    grid-template-columns: 1fr;
    justify-items: center;
  }

  .qr-side {
    justify-items: center;
    text-align: center;
  }
}

@media (max-width: 420px) {
  .login-proxy-options {
    grid-template-columns: 1fr;
  }
}
</style>
