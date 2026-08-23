import { api } from './client'
import type {
  AccountCategory,
  AccountBatchOperationResult,
  AccountChatMembership,
  AccountDetail,
  AccountListItem,
  AccountProxyBatchResult,
  AccountProxyBindingRequest,
  AccountProxyEgress,
  AccountProxyStrategy,
  AccountImportProxyStrategy,
  BatchChangeRecoveryEmailRequest,
  BucketBackupResult,
  BucketBackupSettings,
  CleanupWasteResult,
  AuthMe,
  BatchTask,
  CreateScheduledTaskRequest,
  CreateChatMembershipTaskRequest,
  CreateTaskRequest,
  UpdateScheduledTaskRequest,
  UpdateTaskRequest,
  DashboardSummary,
  DataDictionary,
  EmailOperationResult,
  ImportAccountsResponse,
  LoginEmailStatus,
  AccountLoginResponse,
  AccountQrLoginResponse,
  BotChatOption,
  ChannelAdminDefaults,
  BotAdminRightsPayload,
  BotChannelDetail,
  BotChannelListItem,
  BotChannelStatusResult,
  BotManagementItem,
  BotOption,
  ChannelDetail,
  ChatAdmin,
  ChannelListItem,
  OperationResult,
  ExternalApiCenter,
  ExternalApiDefinition,
  GroupDetail,
  GroupListItem,
  GlobalProxySettings,
  ProxyCategory,
  SaveGlobalProxySettingsRequest,
  LinkResult,
  OperationAccount,
  PagedResult,
  ModuleCenter,
  ModuleOperationResult,
  SaveTextDictionaryRequest,
  SaveExternalApiRequest,
  SaveBucketBackupSettings,
  ScheduledTask,
  AiSettings,
  AiTestResult,
  BatchSettings,
  BotAutoSyncSettings,
  ChatMembershipRiskResult,
  CloudMailSettings,
  LoggingSettings,
  SimpleCategory,
  ModuleNavItem,
  SettingsPayload,
  SyncResult,
  SyncSettings,
  TelegramStatusAutoRefreshSettings,
  TaskCenter,
  TaskAssetUploadResult,
  TelegramAuthorization,
  TelegramApiSettings,
  TelegramDeviceProfile,
  TelegramStatus,
  TelegramSystemMessage,
  TimeZoneSettings,
  TwoFactorRecoveryEmailStatus,
  VersionApplyResult,
  VersionInfo,
  UpdateModeResult,
  SystemRestartResult,
  NumberPreset,
  NetworkEgress,
  OutboundProxy,
  ProxyBatchResult,
  ProxyImportRequest,
  SaveOutboundProxyRequest,
  TextPreset,
  CreateWarpProxyRequest,
  WarpMaintenanceBatchResult,
  WarpMaintenanceResult,
  WarpRuntimeStatus,
} from './types'

const PROXY_SAVE_TIMEOUT_MS = 120_000
const PROXY_DELETE_TIMEOUT_MS = 900_000
const PROXY_IMPORT_TIMEOUT_MS = 900_000
const WARP_OPERATION_TIMEOUT_MS = 900_000

export const panelApi = {
  me: () => api.get<AuthMe>('/auth/me').then((r) => r.data),
  login: (username: string, password: string) =>
    api.post<AuthMe>('/auth/login', { username, password }).then((r) => r.data),
  logout: () => api.post<OperationResult>('/auth/logout').then((r) => r.data),

  summary: () => api.get<DashboardSummary>('/summary').then((r) => r.data),
  networkEgress: () => api.get<NetworkEgress>('/network/egress').then((r) => r.data),
  proxies: () => api.get<OutboundProxy[]>('/proxies').then((r) => r.data),
  proxyCategories: () => api.get<ProxyCategory[]>('/proxy-categories').then((r) => r.data),
  createProxyCategory: (payload: { name: string; color?: string | null; description?: string | null }) =>
    api.post<ProxyCategory>('/proxy-categories', payload).then((r) => r.data),
  updateProxyCategory: (id: number, payload: { name: string; color?: string | null; description?: string | null }) =>
    api.put<ProxyCategory>(`/proxy-categories/${id}`, payload).then((r) => r.data),
  deleteProxyCategory: (id: number) =>
    api.delete<OperationResult>(`/proxy-categories/${id}`).then((r) => r.data),
  batchSetProxyCategory: (proxyIds: number[], categoryId: number | null) =>
    api.post<OperationResult>('/proxies/batch/category', { proxyIds, categoryId }).then((r) => r.data),
  createProxy: (payload: SaveOutboundProxyRequest) =>
    api.post<OutboundProxy>('/proxies', payload, { timeout: PROXY_SAVE_TIMEOUT_MS }).then((r) => r.data),
  updateProxy: (id: number, payload: SaveOutboundProxyRequest) =>
    api.put<OutboundProxy>(`/proxies/${id}`, payload, { timeout: PROXY_SAVE_TIMEOUT_MS }).then((r) => r.data),
  deleteProxy: (id: number) =>
    api.delete<OperationResult>(`/proxies/${id}`, { timeout: PROXY_DELETE_TIMEOUT_MS }).then((r) => r.data),
  batchDeleteProxies: (proxyIds: number[]) =>
    api.post<ProxyBatchResult>('/proxies/batch/delete', { proxyIds }, { timeout: PROXY_DELETE_TIMEOUT_MS }).then((r) => r.data),
  testProxy: (id: number) => api.post<OutboundProxy>(`/proxies/${id}/test`, {}, { timeout: 60_000 }).then((r) => r.data),
  importProxies: (payload: ProxyImportRequest) =>
    api.post<OutboundProxy[]>('/proxies/import', payload, { timeout: PROXY_IMPORT_TIMEOUT_MS }).then((r) => r.data),
  warpStatus: () => api.get<WarpRuntimeStatus>('/proxies/warp/status').then((r) => r.data),
  createWarpProxies: (payload: CreateWarpProxyRequest) =>
    api.post<OutboundProxy>('/proxies/warp', payload, { timeout: WARP_OPERATION_TIMEOUT_MS }).then((r) => r.data),
  refreshWarpProxy: (id: number) =>
    api.post<WarpMaintenanceResult>(`/proxies/${id}/warp/refresh`, {}, { timeout: WARP_OPERATION_TIMEOUT_MS }).then((r) => r.data),
  refreshAllWarpProxies: () =>
    api.post<WarpMaintenanceBatchResult>('/proxies/warp/refresh-all', {}, { timeout: WARP_OPERATION_TIMEOUT_MS }).then((r) => r.data),
  accountCategories: () => api.get<AccountCategory[]>('/account-categories').then((r) => r.data),
  createAccountCategory: (payload: { name: string; color?: string | null; description?: string | null; excludeFromOperations: boolean }) =>
    api.post<AccountCategory>('/account-categories', payload).then((r) => r.data),
  updateAccountCategory: (id: number, payload: { name: string; color?: string | null; description?: string | null; excludeFromOperations: boolean }) =>
    api.put<AccountCategory>(`/account-categories/${id}`, payload).then((r) => r.data),
  deleteAccountCategory: (id: number) =>
    api.delete<OperationResult>(`/account-categories/${id}`).then((r) => r.data),
  saveAccountCategoryAssignments: (id: number, accountIds: number[]) =>
    api.post<OperationResult>(`/account-categories/${id}/assignments`, { accountIds }).then((r) => r.data),
  accounts: (params: {
    page: number
    pageSize: number
    categoryId?: number | null
    search?: string
    onlyWaste?: boolean
  }) => api.get<PagedResult<AccountListItem>>('/accounts', { params }).then((r) => r.data),
  setAccountProxy: (id: number, payload: AccountProxyBindingRequest) =>
    api.post<AccountProxyBatchResult>(`/accounts/${id}/proxy`, payload, { timeout: 900_000 }).then((r) => r.data),
  batchSetAccountProxy: (accountIds: number[], payload: AccountProxyBindingRequest) =>
    api.post<AccountProxyBatchResult>('/accounts/batch/proxy', { accountIds, ...payload }, { timeout: 900_000 }).then((r) => r.data),
  accountProxyEgress: (id: number) =>
    api.get<AccountProxyEgress>(`/accounts/${id}/proxy/egress`, { timeout: 60_000 }).then((r) => r.data),
  account: (id: number) => api.get<AccountDetail>(`/accounts/${id}`).then((r) => r.data),
  updateAccount: (id: number, payload: { remark?: string | null; twoFactorPassword?: string | null; categoryId?: number | null; deviceProfileKey?: string | null }) =>
    api.put<AccountDetail>(`/accounts/${id}`, payload).then((r) => r.data),
  setAccountActive: (id: number, isActive: boolean) =>
    api.patch<OperationResult>(`/accounts/${id}/active`, { isActive }).then((r) => r.data),
  deleteAccount: (id: number) => api.delete<OperationResult>(`/accounts/${id}`).then((r) => r.data),
  refreshTelegramStatus: (id: number) =>
    api.post<TelegramStatus>(`/accounts/${id}/telegram-status`, { probeCreateChannel: false }, { timeout: 300_000 }).then((r) => r.data),
  refreshTelegramStatusWithProbe: (id: number, probeCreateChannel: boolean) =>
    api.post<TelegramStatus>(`/accounts/${id}/telegram-status`, { probeCreateChannel }, { timeout: 300_000 }).then((r) => r.data),
  batchRefreshTelegramStatus: (accountIds: number[], probeCreateChannel: boolean) =>
    api.post<AccountBatchOperationResult>('/accounts/telegram-status', { accountIds, probeCreateChannel }).then((r) => r.data),
  batchSetAccountCategory: (accountIds: number[], categoryId: number | null) =>
    api.post<OperationResult>('/accounts/batch/category', { accountIds, categoryId }).then((r) => r.data),
  batchDeleteAccounts: (accountIds: number[]) =>
    api.post<AccountBatchOperationResult>('/accounts/batch/delete', { accountIds }).then((r) => r.data),
  batchKickAllOtherDevices: (accountIds: number[]) =>
    api.post<AccountBatchOperationResult>('/accounts/batch/kick-devices', { accountIds }).then((r) => r.data),
  cleanupWasteAccounts: (payload: {
    scope: 'selected' | 'filtered' | 'all'
    accountIds?: number[]
    categoryId?: number | null
    search?: string
    probeCreateChannel?: boolean
  }) => api.post<CleanupWasteResult>('/accounts/cleanup-waste', payload).then((r) => r.data),
  systemMessages: (id: number, limit = 30) =>
    api.get<TelegramSystemMessage[]>(`/accounts/${id}/system-messages`, { params: { limit } }).then((r) => r.data),
  devices: (id: number) => api.get<TelegramAuthorization[]>(`/accounts/${id}/devices`).then((r) => r.data),
  kickDevice: (id: number, hash: string) =>
    api.post<OperationResult>(`/accounts/${id}/devices/${encodeURIComponent(hash)}/kick`).then((r) => r.data),
  kickAllOtherDevices: (id: number) =>
    api.post<OperationResult>(`/accounts/${id}/devices/kick-all`).then((r) => r.data),
  accountChannels: (id: number) =>
    api.get<AccountChatMembership[]>(`/accounts/${id}/channels`).then((r) => r.data),
  accountGroups: (id: number) =>
    api.get<AccountChatMembership[]>(`/accounts/${id}/groups`).then((r) => r.data),
  checkChatMembershipRisk: (accountIds: number[]) =>
    api.post<ChatMembershipRiskResult>('/accounts/chat-membership/risk-check', { accountIds }).then((r) => r.data),
  changeChatMembership: (payload: {
    accountIds: number[]
    operation: 'join' | 'leave'
    links: string[]
    treatNoBotSuffixAsBot?: boolean
    delayMs?: number | null
  }) => api.post<AccountBatchOperationResult>('/accounts/chat-membership', payload, { timeout: 300_000 }).then((r) => r.data),
  createChatMembershipTask: (payload: CreateChatMembershipTaskRequest) => {
    const links = Array.from(new Set(payload.links.map((x) => x.trim()).filter(Boolean)))
    const total = Math.max(0, payload.accountIds.length * links.length)
    return api.post<BatchTask>('/tasks', {
      taskType: 'user_join_subscribe',
      total,
      config: JSON.stringify({
        accountIds: Array.from(new Set(payload.accountIds.filter((x) => x > 0))),
        operation: payload.operation,
        links,
        treatNoBotSuffixAsBot: payload.treatNoBotSuffixAsBot === true,
        delayMs: payload.delayMs ?? 2000,
      }),
    }).then((r) => r.data)
  },
  updateAccountProfile: (id: number, form: FormData) =>
    api.post<OperationResult>(`/accounts/${id}/profile`, form, { timeout: 120_000 }).then((r) => r.data),
  batchUpdateProfile: (payload: {
    accountIds: number[]
    mode: 'nickname' | 'bio' | 'username'
    nickname?: string | null
    bio?: string | null
    usernameTemplate?: string | null
    nicknameTemplates?: string[] | null
    appendPhoneLast4WhenDuplicate?: boolean | null
  }) => api.post<AccountBatchOperationResult>('/accounts/batch/profile', payload, { timeout: 120_000 }).then((r) => r.data),
  batchUpdateAvatar: (form: FormData) =>
    api.post<AccountBatchOperationResult>('/accounts/batch/avatar', form, { timeout: 300_000 }).then((r) => r.data),
  changeTwoFactorPassword: (payload: {
    accountIds: number[]
    currentPassword?: string | null
    newPassword: string
    hint?: string | null
    useStoredPasswords?: boolean | null
    saveNewPasswordToDb?: boolean | null
  }) =>
    api.post<AccountBatchOperationResult>('/accounts/two-factor/password', payload, { timeout: 120_000 }).then((r) => r.data),
  requestTwoFactorPasswordReset: (accountIds: number[]) =>
    api.post<AccountBatchOperationResult>('/accounts/two-factor/password/reset', { accountIds }, { timeout: 120_000 }).then((r) => r.data),
  twoFactorRecoveryEmailStatus: (id: number) =>
    api.get<TwoFactorRecoveryEmailStatus>(`/accounts/${id}/two-factor/recovery-email`).then((r) => r.data),
  setTwoFactorRecoveryEmail: (id: number, payload: { currentPassword?: string | null; email: string }) =>
    api.post<EmailOperationResult>(`/accounts/${id}/two-factor/recovery-email`, payload, { timeout: 120_000 }).then((r) => r.data),
  confirmTwoFactorRecoveryEmail: (id: number, code: string) =>
    api.post<OperationResult>(`/accounts/${id}/two-factor/recovery-email/confirm`, { code }).then((r) => r.data),
  resendTwoFactorRecoveryEmail: (id: number) =>
    api.post<EmailOperationResult>(`/accounts/${id}/two-factor/recovery-email/resend`, {}, { timeout: 120_000 }).then((r) => r.data),
  cancelTwoFactorRecoveryEmail: (id: number) =>
    api.post<OperationResult>(`/accounts/${id}/two-factor/recovery-email/cancel`, {}, { timeout: 120_000 }).then((r) => r.data),
  loginEmailStatus: (id: number) =>
    api.get<LoginEmailStatus>(`/accounts/${id}/login-email`).then((r) => r.data),
  setLoginEmail: (id: number, email: string) =>
    api.post<EmailOperationResult>(`/accounts/${id}/login-email`, { email }, { timeout: 120_000 }).then((r) => r.data),
  confirmLoginEmail: (id: number, code: string) =>
    api.post<OperationResult>(`/accounts/${id}/login-email/confirm`, { code }).then((r) => r.data),
  batchChangeRecoveryEmail: (payload: BatchChangeRecoveryEmailRequest) =>
    api.post<AccountBatchOperationResult>('/accounts/batch/recovery-email', payload, { timeout: 900_000 }).then((r) => r.data),
  importAccountsZip: (form: FormData) =>
    api.post<ImportAccountsResponse>('/accounts/import/zip', form, { timeout: 900_000 }).then((r) => r.data),
  importAccountsSessionFiles: (form: FormData) =>
    api.post<ImportAccountsResponse>('/accounts/import/session-files', form, { timeout: 900_000 }).then((r) => r.data),
  importAccountsStringSession: (payload: {
    sessionString: string
    categoryId?: number | null
    proxyStrategy: AccountImportProxyStrategy
    proxyId?: number | null
    deviceProfileKey?: string | null
  }) =>
    api.post<ImportAccountsResponse>('/accounts/import/string-session', payload, { timeout: 900_000 }).then((r) => r.data),
  startAccountLogin: (payload: {
    phone: string
    loginId?: number
    proxyStrategy: AccountProxyStrategy
    proxyId?: number | null
    deviceProfileKey?: string | null
  }) =>
    api.post<AccountLoginResponse>('/accounts/login/start', payload, { timeout: 900_000 }).then((r) => r.data),
  startAccountQrLogin: (payload: {
    loginId?: number
    proxyStrategy: AccountProxyStrategy
    proxyId?: number | null
    deviceProfileKey?: string | null
  }) =>
    api.post<AccountQrLoginResponse>('/accounts/login/qr/start', { ...payload, loginId: payload.loginId || 0 }, { timeout: 900_000 }).then((r) => r.data),
  pollAccountQrLogin: (loginId: number) =>
    api.post<AccountQrLoginResponse>('/accounts/login/qr/poll', { loginId }, { timeout: 120_000 }).then((r) => r.data),
  submitAccountQrLoginPassword: (loginId: number, password: string, saveTwoFactorPassword = false) =>
    api.post<AccountQrLoginResponse>('/accounts/login/qr/password', { loginId, password, saveTwoFactorPassword }, { timeout: 120_000 }).then((r) => r.data),
  cancelAccountQrLogin: (loginId: number) =>
    api.post<OperationResult>('/accounts/login/qr/cancel', { loginId }).then((r) => r.data),
  submitAccountLoginCode: (loginId: number, code: string) =>
    api.post<AccountLoginResponse>('/accounts/login/code', { loginId, code }, { timeout: 120_000 }).then((r) => r.data),
  resendAccountLoginCode: (loginId: number) =>
    api.post<AccountLoginResponse>('/accounts/login/resend', { loginId }, { timeout: 120_000 }).then((r) => r.data),
  submitAccountLoginPassword: (loginId: number, password: string, saveTwoFactorPassword = false) =>
    api.post<AccountLoginResponse>('/accounts/login/password', { loginId, password, saveTwoFactorPassword }, { timeout: 120_000 }).then((r) => r.data),
  resetAccountLogin: (loginId: number) =>
    api.post<OperationResult>('/accounts/login/reset', { loginId }).then((r) => r.data),

  settings: () => api.get<SettingsPayload>('/settings').then((r) => r.data),
  saveTelegramApiSettings: (payload: TelegramApiSettings) =>
    api.post<OperationResult>('/settings/telegram-api', payload).then((r) => r.data),
  deviceProfiles: () => api.get<{ items: TelegramDeviceProfile[]; defaultKey: string }>('/settings/device-profiles').then((r) => r.data),
  globalProxySettings: () =>
    api.get<GlobalProxySettings>('/settings/global-proxy').then((r) => r.data),
  saveGlobalProxySettings: (payload: SaveGlobalProxySettingsRequest) =>
    api.post<OperationResult>('/settings/global-proxy', payload).then((r) => r.data),
  saveCloudMailSettings: (payload: CloudMailSettings) =>
    api.post<OperationResult>('/settings/cloud-mail', payload).then((r) => r.data),
  generateCloudMailToken: (payload: { baseUrl: string; adminEmail: string; adminPassword: string }) =>
    api.post<{ token: string }>('/settings/cloud-mail/token', payload, { timeout: 120_000 }).then((r) => r.data),
  saveAiSettings: (payload: AiSettings) =>
    api.post<OperationResult>('/settings/ai', payload).then((r) => r.data),
  testAiSettings: (payload: AiSettings) =>
    api.post<AiTestResult>('/settings/ai/test', payload, { timeout: 120_000 }).then((r) => r.data),
  saveBatchSettings: (payload: BatchSettings) =>
    api.post<OperationResult>('/settings/batch', payload).then((r) => r.data),
  saveTimeZoneSettings: (payload: TimeZoneSettings) =>
    api.post<OperationResult>('/settings/time-zone', payload).then((r) => r.data),
  saveSyncSettings: (payload: SyncSettings) =>
    api.post<OperationResult>('/settings/sync', payload).then((r) => r.data),
  startSyncNow: () =>
    api.post<OperationResult>('/settings/sync-now', {}, { timeout: 120_000 }).then((r) => r.data),
  saveBotAutoSyncSettings: (payload: BotAutoSyncSettings) =>
    api.post<OperationResult>('/settings/bot-auto-sync', payload).then((r) => r.data),
  saveTelegramStatusSettings: (payload: TelegramStatusAutoRefreshSettings) =>
    api.post<OperationResult>('/settings/telegram-status', payload).then((r) => r.data),
  saveLoggingSettings: (payload: LoggingSettings) =>
    api.post<OperationResult>('/settings/logging', payload).then((r) => r.data),
  bucketBackupSettings: () => api.get<BucketBackupSettings>('/settings/bucket-backup').then((r) => r.data),
  saveBucketBackupSettings: (payload: SaveBucketBackupSettings) =>
    api.post<OperationResult>('/settings/bucket-backup', payload).then((r) => r.data),
  runBucketBackup: () =>
    api.post<BucketBackupResult>('/settings/bucket-backup/run', {}, { timeout: 300_000 }).then((r) => r.data),
  clearCache: () =>
    api.post<OperationResult>('/settings/cache/clear').then((r) => r.data),
  changeAdminUsername: (payload: { currentPassword: string; newUsername: string }) =>
    api.post<OperationResult>('/settings/username', payload).then((r) => r.data),
  changeAdminPassword: (payload: { currentPassword: string; newPassword: string }) =>
    api.post<OperationResult>('/settings/password', payload).then((r) => r.data),
  verifyAdminPassword: (password: string) =>
    api.post<OperationResult>('/settings/password/verify', { password }).then((r) => r.data),
  versionInfo: () => api.get<VersionInfo>('/version-info').then((r) => r.data),
  checkVersionInfo: () => api.post<VersionInfo>('/version-info/check').then((r) => r.data),
  applyVersionUpdate: () =>
    api.post<VersionApplyResult>('/version-info/apply', {}, { timeout: 300_000 }).then((r) => r.data),
  updateVersionMode: (mode: 'auto' | 'image' | 'binary') =>
    api.post<UpdateModeResult>('/version-info/mode', { mode }).then((r) => r.data),
  restartPanel: () =>
    api.post<SystemRestartResult>('/system/restart').then((r) => r.data),

  tasks: (count = 50) => api.get<TaskCenter>('/tasks', { params: { count } }).then((r) => r.data),
  task: (id: number) => api.get<BatchTask>(`/tasks/${id}`).then((r) => r.data),
  moduleNav: () => api.get<ModuleNavItem[]>('/module-nav').then((r) => r.data),
  createTask: (payload: CreateTaskRequest) =>
    api.post<BatchTask>('/tasks', payload).then((r) => r.data),
  updateTask: (id: number, payload: UpdateTaskRequest) =>
    api.patch<BatchTask>(`/tasks/${id}`, payload).then((r) => r.data),
  createScheduledTask: (payload: CreateScheduledTaskRequest) =>
    api.post<ScheduledTask>('/scheduled-tasks', payload).then((r) => r.data),
  scheduledTask: (id: number) => api.get<ScheduledTask>(`/scheduled-tasks/${id}`).then((r) => r.data),
  updateScheduledTask: (id: number, payload: UpdateScheduledTaskRequest) =>
    api.put<ScheduledTask>(`/scheduled-tasks/${id}`, payload).then((r) => r.data),
  runScheduledTaskNow: (id: number) =>
    api.post<BatchTask>(`/scheduled-tasks/${id}/run-now`).then((r) => r.data),
  cleanupTasks: (mode: 'history' | 'all') =>
    api.post<OperationResult>('/tasks/cleanup', { mode }).then((r) => r.data),
  pauseTask: (id: number) => api.post<OperationResult>(`/tasks/${id}/pause`).then((r) => r.data),
  resumeTask: (id: number) => api.post<OperationResult>(`/tasks/${id}/resume`).then((r) => r.data),
  cancelTask: (id: number) => api.post<OperationResult>(`/tasks/${id}/cancel`).then((r) => r.data),
  deleteTask: (id: number) => api.delete<OperationResult>(`/tasks/${id}`).then((r) => r.data),
  uploadTaskAvatarAsset: (form: FormData) =>
    api.post<TaskAssetUploadResult>('/tasks/assets/avatar', form, { timeout: 120_000 }).then((r) => r.data),
  pauseScheduledTask: (id: number) =>
    api.post<OperationResult>(`/scheduled-tasks/${id}/pause`).then((r) => r.data),
  resumeScheduledTask: (id: number) =>
    api.post<OperationResult>(`/scheduled-tasks/${id}/resume`).then((r) => r.data),
  deleteScheduledTask: (id: number) =>
    api.delete<OperationResult>(`/scheduled-tasks/${id}`).then((r) => r.data),

  dictionaries: () => api.get<DataDictionary[]>('/data-dictionaries').then((r) => r.data),
  saveTextDictionary: (payload: SaveTextDictionaryRequest) =>
    api.post<DataDictionary>('/data-dictionaries/text', payload).then((r) => r.data),
  saveImageDictionary: (form: FormData) =>
    api.post<DataDictionary>('/data-dictionaries/image', form, { timeout: 300_000 }).then((r) => r.data),
  setDictionaryEnabled: (id: number, isEnabled: boolean) =>
    api.patch<OperationResult>(`/data-dictionaries/${id}/enabled`, { isEnabled }).then((r) => r.data),
  resetDictionaryQueue: (id: number) =>
    api.post<OperationResult>(`/data-dictionaries/${id}/reset-queue`).then((r) => r.data),
  deleteDictionary: (id: number) =>
    api.delete<OperationResult>(`/data-dictionaries/${id}`).then((r) => r.data),

  modules: () => api.get<ModuleCenter>('/modules').then((r) => r.data),
  installModule: (form: FormData) =>
    api.post<ModuleOperationResult>('/modules/install', form, { timeout: 300_000 }).then((r) => r.data),
  enableModule: (id: string, autoRestart: boolean) =>
    api.post<OperationResult>(`/modules/${encodeURIComponent(id)}/enable`, { autoRestart }).then((r) => r.data),
  disableModule: (id: string, autoRestart: boolean) =>
    api.post<OperationResult>(`/modules/${encodeURIComponent(id)}/disable`, { autoRestart }).then((r) => r.data),
  pruneModuleVersions: (id: string, autoRestart: boolean) =>
    api.post<OperationResult>(`/modules/${encodeURIComponent(id)}/prune`, { autoRestart }).then((r) => r.data),
  deleteModule: (id: string, autoRestart: boolean) =>
    api.delete<OperationResult>(`/modules/${encodeURIComponent(id)}`, { params: { autoRestart } }).then((r) => r.data),

  externalApis: () => api.get<ExternalApiCenter>('/external-apis').then((r) => r.data),
  saveExternalApi: (payload: SaveExternalApiRequest) =>
    api.post<ExternalApiDefinition>('/external-apis', payload).then((r) => r.data),
  deleteExternalApi: (id: string) =>
    api.delete<OperationResult>(`/external-apis/${encodeURIComponent(id)}`).then((r) => r.data),
  externalApiBots: () => api.get<BotOption[]>('/external-apis/bots').then((r) => r.data),
  externalApiBotChats: (botId: number) =>
    api.get<BotChatOption[]>(`/external-apis/bots/${botId}/chats`).then((r) => r.data),

  operationAccounts: () => api.get<OperationAccount[]>('/operation-accounts').then((r) => r.data),

  channels: (params: {
    page: number
    pageSize: number
    accountId?: number | null
    groupId?: number | null
    filterType?: string
    membershipRole?: string
    search?: string
  }) => api.get<PagedResult<ChannelListItem>>('/channels', { params }).then((r) => r.data),
  channelDetail: (id: number) => api.get<ChannelDetail>(`/channels/${id}`).then((r) => r.data),
  channelAdmins: (id: number) =>
    api.get<ChatAdmin[]>(`/channels/${id}/admins`, { timeout: 120_000 }).then((r) => r.data),
  createChannel: (payload: {
    accountId: number
    groupId?: number | null
    title: string
    about?: string | null
    isPublic: boolean
    username?: string | null
    allowForwarding: boolean
    ignoreRiskWarning: boolean
  }) => api.post<ChannelListItem>('/channels', payload, { timeout: 120_000 }).then((r) => r.data),
  updateChannel: (id: number, form: FormData) =>
    api.put<ChannelListItem>(`/channels/${id}`, form, { timeout: 120_000 }).then((r) => r.data),
  setChannelGroup: (id: number, categoryId: number | null) =>
    api.patch<OperationResult>(`/channels/${id}/group`, { categoryId }).then((r) => r.data),
  deleteChannel: (id: number) => api.delete<OperationResult>(`/channels/${id}`).then((r) => r.data),
  batchSetChannelGroup: (ids: number[], categoryId: number | null) =>
    api.post<OperationResult>('/channels/batch/group', { ids, categoryId }, { timeout: 120_000 }).then((r) => r.data),
  batchDeleteChannels: (ids: number[]) =>
    api.post<OperationResult>('/channels/batch/delete', { ids }).then((r) => r.data),
  batchInviteChannels: (payload: { ids: number[]; usernames: string[]; accountId?: number | null; accountCategoryId?: number | null; delayMs?: number | null }) =>
    api.post<BatchTask>('/channels/batch/invite', payload).then((r) => r.data),
  batchSetChannelAdmins: (payload: {
    ids: number[]
    usernames: string[]
    accountId?: number | null
    rights: number
    adminTitle?: string | null
    delayMs?: number | null
  }) => api.post<AccountBatchOperationResult>('/channels/batch/admins', payload, { timeout: 300_000 }).then((r) => r.data),
  batchKickChannelUsers: (payload: { ids: number[]; target: string; accountId?: number | null; permanentBan: boolean }) =>
    api.post<AccountBatchOperationResult>('/channels/batch/kick', payload, { timeout: 300_000 }).then((r) => r.data),
  syncChannels: (accountId?: number | null) =>
    api.post<SyncResult>('/channels/sync', { accountId: accountId || null }, { timeout: 120_000 }).then((r) => r.data),
  exportChannelLink: (id: number) =>
    api.post<LinkResult>(`/channels/${id}/export-link`, {}, { timeout: 120_000 }).then((r) => r.data),
  leaveChannel: (id: number) =>
    api.post<OperationResult>(`/channels/${id}/leave`, {}, { timeout: 120_000 }).then((r) => r.data),
  disbandChannel: (id: number) =>
    api.post<OperationResult>(`/channels/${id}/disband`, {}, { timeout: 120_000 }).then((r) => r.data),
  transferChannelOwner: (id: number, payload: { target: string; password?: string | null; accountId?: number | null; targetAccountId?: number | null }) =>
    api.post<OperationResult>(`/channels/${id}/transfer-owner`, payload, { timeout: 120_000 }).then((r) => r.data),

  channelGroups: () => api.get<SimpleCategory[]>('/channel-groups').then((r) => r.data),
  createChannelGroup: (payload: { name: string; description?: string | null }) =>
    api.post<SimpleCategory>('/channel-groups', payload).then((r) => r.data),
  updateChannelGroup: (id: number, payload: { name: string; description?: string | null }) =>
    api.put<SimpleCategory>(`/channel-groups/${id}`, payload).then((r) => r.data),
  deleteChannelGroup: (id: number) => api.delete<OperationResult>(`/channel-groups/${id}`).then((r) => r.data),
  saveChannelGroupAssignments: (id: number, scopeIds: number[], selectedIds: number[]) =>
    api.post<OperationResult>(`/channel-groups/${id}/assignments`, { scopeIds, selectedIds }, { timeout: 120_000 }).then((r) => r.data),

  groups: (params: {
    page: number
    pageSize: number
    accountId?: number | null
    categoryId?: number | null
    filterType?: string
    membershipRole?: string
    search?: string
  }) => api.get<PagedResult<GroupListItem>>('/groups', { params }).then((r) => r.data),
  groupDetail: (id: number) => api.get<GroupDetail>(`/groups/${id}`).then((r) => r.data),
  groupAdmins: (id: number) =>
    api.get<ChatAdmin[]>(`/groups/${id}/admins`, { timeout: 120_000 }).then((r) => r.data),
  kickGroupAdmin: (id: number, userId: number) =>
    api.post<OperationResult>(`/groups/${id}/admins/${userId}/kick`, {}, { timeout: 120_000 }).then((r) => r.data),
  createGroup: (payload: {
    accountId: number
    categoryId?: number | null
    title: string
    about?: string | null
    isPublic: boolean
    username?: string | null
    ignoreRiskWarning: boolean
  }) => api.post<GroupListItem>('/groups', payload, { timeout: 120_000 }).then((r) => r.data),
  updateGroup: (id: number, form: FormData) =>
    api.put<GroupListItem>(`/groups/${id}`, form, { timeout: 120_000 }).then((r) => r.data),
  setGroupCategory: (id: number, categoryId: number | null) =>
    api.patch<OperationResult>(`/groups/${id}/category`, { categoryId }).then((r) => r.data),
  deleteGroup: (id: number) => api.delete<OperationResult>(`/groups/${id}`).then((r) => r.data),
  batchSetGroupCategory: (ids: number[], categoryId: number | null) =>
    api.post<OperationResult>('/groups/batch/category', { ids, categoryId }, { timeout: 120_000 }).then((r) => r.data),
  batchDeleteGroups: (ids: number[]) =>
    api.post<OperationResult>('/groups/batch/delete', { ids }).then((r) => r.data),
  batchInviteGroups: (payload: { ids: number[]; usernames: string[]; accountId?: number | null; accountCategoryId?: number | null; delayMs?: number | null }) =>
    api.post<BatchTask>('/groups/batch/invite', payload).then((r) => r.data),
  batchSetGroupAdmins: (payload: {
    ids: number[]
    usernames: string[]
    accountId?: number | null
    rights: number
    adminTitle?: string | null
    delayMs?: number | null
  }) => api.post<AccountBatchOperationResult>('/groups/batch/admins', payload, { timeout: 300_000 }).then((r) => r.data),
  batchKickGroupUsers: (payload: { ids: number[]; target: string; accountId?: number | null; permanentBan: boolean }) =>
    api.post<AccountBatchOperationResult>('/groups/batch/kick', payload, { timeout: 300_000 }).then((r) => r.data),
  syncGroups: (accountId?: number | null) =>
    api.post<SyncResult>('/groups/sync', { accountId: accountId || null }, { timeout: 120_000 }).then((r) => r.data),
  exportGroupLink: (id: number) =>
    api.post<LinkResult>(`/groups/${id}/export-link`, {}, { timeout: 120_000 }).then((r) => r.data),
  leaveGroup: (id: number) =>
    api.post<OperationResult>(`/groups/${id}/leave`, {}, { timeout: 120_000 }).then((r) => r.data),
  disbandGroup: (id: number) =>
    api.post<OperationResult>(`/groups/${id}/disband`, {}, { timeout: 120_000 }).then((r) => r.data),
  transferGroupOwner: (id: number, payload: { target: string; password?: string | null; accountId?: number | null; targetAccountId?: number | null }) =>
    api.post<OperationResult>(`/groups/${id}/transfer-owner`, payload, { timeout: 120_000 }).then((r) => r.data),

  groupCategories: () => api.get<SimpleCategory[]>('/group-categories').then((r) => r.data),
  createGroupCategory: (payload: { name: string; description?: string | null }) =>
    api.post<SimpleCategory>('/group-categories', payload).then((r) => r.data),
  updateGroupCategory: (id: number, payload: { name: string; description?: string | null }) =>
    api.put<SimpleCategory>(`/group-categories/${id}`, payload).then((r) => r.data),
  deleteGroupCategory: (id: number) => api.delete<OperationResult>(`/group-categories/${id}`).then((r) => r.data),
  saveGroupCategoryAssignments: (id: number, scopeIds: number[], selectedIds: number[]) =>
    api.post<OperationResult>(`/group-categories/${id}/assignments`, { scopeIds, selectedIds }, { timeout: 120_000 }).then((r) => r.data),

  bots: () => api.get<BotManagementItem[]>('/bots').then((r) => r.data),
  createBot: (payload: { name: string; token: string; username?: string | null }) =>
    api.post<BotManagementItem>('/bots', payload, { timeout: 120_000 }).then((r) => r.data),
  updateBot: (id: number, payload: { name: string; token?: string | null; username?: string | null }) =>
    api.put<BotManagementItem>(`/bots/${id}`, payload, { timeout: 120_000 }).then((r) => r.data),
  setBotActive: (id: number, isActive: boolean) =>
    api.patch<OperationResult>(`/bots/${id}/active`, { isActive }, { timeout: 120_000 }).then((r) => r.data),
  deleteBot: (id: number) => api.delete<OperationResult>(`/bots/${id}`).then((r) => r.data),

  botChannelCategories: () => api.get<SimpleCategory[]>('/bot-channel-categories').then((r) => r.data),
  createBotChannelCategory: (payload: { name: string; description?: string | null }) =>
    api.post<SimpleCategory>('/bot-channel-categories', payload).then((r) => r.data),
  updateBotChannelCategory: (id: number, payload: { name: string; description?: string | null }) =>
    api.put<SimpleCategory>(`/bot-channel-categories/${id}`, payload).then((r) => r.data),
  deleteBotChannelCategory: (id: number) =>
    api.delete<OperationResult>(`/bot-channel-categories/${id}`).then((r) => r.data),
  botChannels: (params: {
    page: number
    pageSize: number
    botId?: number | null
    categoryId?: number | null
    status?: number | null
    search?: string
  }) => api.get<PagedResult<BotChannelListItem>>('/bot-channels', { params }).then((r) => r.data),
  botChannelDetail: (id: number, botId: number) =>
    api.get<BotChannelDetail>(`/bot-channels/${id}`, { params: { botId }, timeout: 120_000 }).then((r) => r.data),
  botChannelAdmins: (id: number, botId: number) =>
    api.get<ChatAdmin[]>(`/bot-channels/${id}/admins`, { params: { botId }, timeout: 120_000 }).then((r) => r.data),
  updateBotChannel: (id: number, botId: number, form: FormData) =>
    api.put<BotChannelListItem>(`/bot-channels/${id}`, form, { params: { botId }, timeout: 120_000 }).then((r) => r.data),
  setBotChannelCategory: (id: number, categoryId: number | null) =>
    api.patch<OperationResult>(`/bot-channels/${id}/category`, { categoryId }).then((r) => r.data),
  batchSetBotChannelCategory: (ids: number[], categoryId: number | null) =>
    api.post<OperationResult>('/bot-channels/batch/category', { ids, categoryId }).then((r) => r.data),
  batchDeleteBotChannels: (ids: number[], botIds?: number[]) =>
    api.post<OperationResult>('/bot-channels/batch/delete', { ids, botIds: botIds || [] }).then((r) => r.data),
  checkBotChannelStatus: (botId: number, ids: number[]) =>
    api.post<BotChannelStatusResult>('/bot-channels/batch/status', { botId, ids }, { timeout: 180_000 }).then((r) => r.data),
  inviteBotChannelMembers: (payload: { botId: number; ids: number[]; usernames: string[]; selectedAccountId: number; accountCategoryId?: number | null; delayMs?: number | null }) =>
    api.post<BatchTask>('/bot-channels/batch/invite', payload).then((r) => r.data),
  banBotChannelMembers: (payload: {
    botId: number
    ids: number[]
    target: string
    permanentBan: boolean
    useAccountExecution: boolean
    selectedAccountId: number
  }) =>
    api.post<AccountBatchOperationResult>('/bot-channels/batch/ban', payload, { timeout: 180_000 }).then((r) => r.data),
  channelInvitePresets: () =>
    api.get<TextPreset[]>('/bot-channels/presets/invite').then((r) => r.data),
  saveChannelInvitePreset: (name: string, values: string[]) =>
    api.post<OperationResult>('/bot-channels/presets/invite', { name, values }).then((r) => r.data),
  deleteChannelInvitePreset: (name: string) =>
    api.delete<OperationResult>(`/bot-channels/presets/invite/${encodeURIComponent(name)}`).then((r) => r.data),
  channelAdminPresets: () =>
    api.get<TextPreset[]>('/bot-channels/presets/admin-usernames').then((r) => r.data),
  saveChannelAdminPreset: (name: string, values: string[]) =>
    api.post<OperationResult>('/bot-channels/presets/admin-usernames', { name, values }).then((r) => r.data),
  deleteChannelAdminPreset: (name: string) =>
    api.delete<OperationResult>(`/bot-channels/presets/admin-usernames/${encodeURIComponent(name)}`).then((r) => r.data),
  botAdminPresets: () =>
    api.get<NumberPreset[]>('/bot-channels/presets/admin-user-ids').then((r) => r.data),
  saveBotAdminPreset: (name: string, values: number[]) =>
    api.post<OperationResult>('/bot-channels/presets/admin-user-ids', { name, values }).then((r) => r.data),
  deleteBotAdminPreset: (name: string) =>
    api.delete<OperationResult>(`/bot-channels/presets/admin-user-ids/${encodeURIComponent(name)}`).then((r) => r.data),
  channelAdminDefaults: () =>
    api.get<ChannelAdminDefaults>('/bot-channels/admin-defaults/account').then((r) => r.data),
  saveChannelAdminDefaults: (rights: number) =>
    api.post<OperationResult>('/bot-channels/admin-defaults/account', { rights }).then((r) => r.data),
  botChannelAdminDefaults: () =>
    api.get<BotAdminRightsPayload>('/bot-channels/admin-defaults/bot').then((r) => r.data),
  saveBotChannelAdminDefaults: (rights: BotAdminRightsPayload) =>
    api.post<OperationResult>('/bot-channels/admin-defaults/bot', rights).then((r) => r.data),
  createBotAdminsByAccountTask: (payload: {
    botId: number
    ids: number[]
    selectedAccountId: number
    usernames: string[]
    rights: number
    adminTitle?: string | null
    delayMs?: number | null
  }) => api.post<BatchTask>('/bot-channels/tasks/admins-by-account', payload).then((r) => r.data),
  createBotAdminsByBotTask: (payload: { botId: number; ids: number[]; userIds: number[]; rights: BotAdminRightsPayload }) =>
    api.post<BatchTask>('/bot-channels/tasks/admins-by-bot', payload).then((r) => r.data),
  syncBotChannels: (botId: number) =>
    api.post<OperationResult>('/bot-channels/sync', { botId }, { timeout: 120_000 }).then((r) => r.data),
  exportBotChannelLink: (id: number, botId: number) =>
    api.post<LinkResult>(`/bot-channels/${id}/export-link`, {}, { params: { botId }, timeout: 120_000 }).then((r) => r.data),
}
