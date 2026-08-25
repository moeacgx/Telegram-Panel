export interface AuthMe {
  authenticated: boolean
  username?: string | null
  mustChangePassword: boolean
  authEnabled: boolean
  version?: string | null
}

export interface OperationResult {
  success: boolean
  message?: string | null
  code?: string | null
}

export interface ProxyBatchItem {
  proxyId: number
  success: boolean
  summary: string
  error?: string | null
}

export interface ProxyBatchResult {
  success: number
  failed: number
  items: ProxyBatchItem[]
}

export type ProxyKind = 'manual' | 'resin' | 'warp' | 'wireguard_warp'

export type ProxyProtocol = 'http' | 'socks5' | 'mtproto'
export type WarpProxyProtocol = Extract<ProxyProtocol, 'http' | 'socks5'>
export type GlobalProxySourceMode = 'manual' | 'existing'

export interface ProxyCategory {
  id: number
  name: string
  color?: string | null
  description?: string | null
  proxyCount?: number
}

export interface NetworkEgress {
  success: boolean
  ip?: string | null
  country?: string | null
  city?: string | null
  isp?: string | null
  warpStatus?: string | null
  latencyMs?: number | null
  checkedAtUtc: string
  error?: string | null
  source?: string | null
}

export interface OutboundProxy {
  id: number
  name: string
  kind: ProxyKind
  protocol: ProxyProtocol
  host: string
  port: number
  username?: string | null
  resinPlatform?: string | null
  resinAdminUrl?: string | null
  hasPassword?: boolean
  hasSecret?: boolean
  hasResinAdminToken?: boolean
  isEnabled: boolean
  testStatus: string
  lastError?: string | null
  lastLatencyMs?: number | null
  egressIp?: string | null
  egressCountry?: string | null
  egressCity?: string | null
  egressIsp?: string | null
  warpStatus?: string | null
  warpRuntimeStatus?: string | null
  warpConsecutiveFailures?: number
  warpLastRecoveryAttemptAtUtc?: string | null
  warpLastRecoveredAtUtc?: string | null
  warpRecoveryCount?: number
  lastTestedAtUtc?: string | null
  firstBoundAtUtc?: string | null
  accountCount?: number
  category?: ProxyCategory | null
  isGlobal?: boolean
  isInUse?: boolean
  usageCount?: number
  createdAtUtc: string
  updatedAtUtc: string
}

export interface SaveOutboundProxyRequest {
  name: string
  kind: 'manual' | 'resin' | 'wireguard_warp'
  protocol: ProxyProtocol
  host: string
  port: number
  username?: string | null
  password?: string | null
  clearPassword?: boolean
  secret?: string | null
  resinPlatform?: string | null
  resinAdminUrl?: string | null
  resinAdminToken?: string | null
  clearResinAdminToken?: boolean
  categoryId?: number | null
  isEnabled: boolean
  testAfterSave: boolean
}

export interface ProxyImportRequest {
  text: string
  testAfterImport: boolean
}

export interface WarpRuntimeStatus {
  platformSupported: boolean
  enabled: boolean
  dockerAvailable: boolean
  dockerVersion?: string | null
  error?: string | null
  image: string
  network: string
  proxyHostMode: string
  defaultProtocol: WarpProxyProtocol
  maintenance?: WarpMaintenanceRuntimeStatus | null
}

export interface WarpMaintenanceRuntimeStatus {
  enabled: boolean
  running: boolean
  healthCheckIntervalMinutes: number
  failureThreshold: number
  recoveryCooldownMinutes: number
  scheduledRefreshEnabled: boolean
  scheduledRefreshIntervalMinutes: number
  lastRunAtUtc?: string | null
  nextRunAtUtc?: string | null
  lastError?: string | null
  checkedCount: number
  healthyCount: number
  recoveredCount: number
  failedCount: number
}

export interface WarpMaintenanceResult {
  proxyId: number
  name: string
  success: boolean
  restarted: boolean
  recovered: boolean
  runtimeStatus: string
  summary: string
  error?: string | null
}

export interface WarpMaintenanceBatchResult {
  checked: number
  healthy: number
  recovered: number
  failed: number
  items: WarpMaintenanceResult[]
}

export interface CreateWarpProxyRequest {
  name?: string | null
  requestId?: string | null
  protocol?: WarpProxyProtocol | null
}

export type AccountProxyStrategy = 'direct' | 'global' | 'existing' | 'warp_per_account' | 'warp_pool'

export type AccountProxyBatchStrategy = AccountProxyStrategy | 'proxy_per_account'

export type AccountImportProxyStrategy = AccountProxyStrategy

export type ZipImportProxyStrategy = AccountImportProxyStrategy | 'proxy_per_account'

export interface AccountProxyBindingRequest {
  strategy: AccountProxyBatchStrategy
  proxyId?: number | null
  expectedProxyId?: number | null
  proxyText?: string | null
}

export interface AccountProxyOperationItem {
  accountId: number
  phone?: string | null
  success: boolean
  summary: string
  error?: string | null
  proxyId?: number | null
}

export interface AccountProxyBatchResult {
  success: number
  failed: number
  items: AccountProxyOperationItem[]
}

export interface AccountProxyEgress extends NetworkEgress {
  accountId?: number
  proxyId?: number | null
  proxyName?: string | null
}

export interface AccountProxySummary {
  id: number
  name: string
  kind: ProxyKind
  protocol: ProxyProtocol
  host: string
  port: number
  resinPlatform?: string | null
  isEnabled: boolean
  testStatus: string
  egressIp?: string | null
}

export interface SystemRestartResult extends OperationResult {
  restartScheduled: boolean
}

export interface PagedResult<T> {
  items: T[]
  total: number
  page: number
  pageSize: number
}

export interface AccountCategory {
  id: number
  name: string
  color?: string | null
  description?: string | null
  excludeFromOperations: boolean
  accountCount: number
}

export interface AccountListItem {
  id: number
  displayNumber: number
  displayPhone: string
  phone?: string
  nickname?: string | null
  username?: string | null
  remark?: string | null
  userId: number
  isActive: boolean
  category?: AccountCategory | null
  channelCount: number
  groupCount: number
  estimatedRegistrationAt?: string | null
  lastSyncAt: string
  telegramStatusSummary?: string | null
  telegramStatusDetails?: string | null
  telegramStatusOk?: boolean | null
  telegramStatusCheckedAtUtc?: string | null
  useGlobalProxy: boolean
  proxy?: AccountProxySummary | null
}

export interface AccountDetail {
  id: number
  displayNumber: number
  displayPhone: string
  phone: string
  nickname?: string | null
  username?: string | null
  remark?: string | null
  userId: number
  sessionPath: string
  twoFactorPassword?: string | null
  categoryId?: number | null
  deviceProfileKey?: string | null
  isActive: boolean
  createdAt: string
  estimatedRegistrationAt?: string | null
  lastSyncAt: string
  lastLoginAt?: string | null
}

export interface TelegramStatus {
  ok: boolean
  summary: string
  details?: string | null
  checkedAtUtc: string
}

export interface TelegramSystemMessage {
  id: number
  dateUtc?: string | null
  text: string
}

export interface TelegramAuthorization {
  hash: string
  current: boolean
  apiId: number
  appName?: string | null
  appVersion?: string | null
  deviceModel?: string | null
  platform?: string | null
  systemVersion?: string | null
  ip?: string | null
  country?: string | null
  region?: string | null
  createdAtUtc?: string | null
  lastActiveAtUtc?: string | null
  title: string
  apiFamily: string
  apiDisplayName: string
  apiDescription: string
  deviceDisplayName: string
}

export interface AccountChatMembership {
  id: number
  title: string
  username?: string | null
  isCreator: boolean
  isAdmin: boolean
  categoryName?: string | null
  memberCount: number
  syncedAt: string
}

export interface AccountOperationItem {
  accountId: number
  phone?: string | null
  success: boolean
  summary: string
  error?: string | null
}

export interface AccountBatchOperationResult {
  success: number
  failed: number
  items: AccountOperationItem[]
}

export interface RiskAccount {
  id: number
  displayNumber: number
  displayPhone: string
  riskReferenceHours?: number | null
  isEstimated: boolean
}

export interface ChatMembershipRiskResult {
  hasRiskyAccounts: boolean
  totalCount: number
  riskyCount: number
  safeCount: number
  message: string
  detailedMessage: string
  riskyAccounts: RiskAccount[]
  safeAccountIds: number[]
}

export interface BotChannelStatusFailure {
  telegramId: number
  error: string
}

export interface BotChannelStatusResult {
  successCount: number
  failedCount: number
  totalCount: number
  failures: BotChannelStatusFailure[]
}

export interface BotAdminRightsPayload {
  manageChat: boolean
  changeInfo: boolean
  postMessages: boolean
  editMessages: boolean
  deleteMessages: boolean
  inviteUsers: boolean
  restrictMembers: boolean
  pinMessages: boolean
  promoteMembers: boolean
}

export interface TextPreset {
  name: string
  values: string[]
}

export interface NumberPreset {
  name: string
  values: number[]
}

export interface ChannelAdminDefaults {
  rights: number
}

export interface SettingsPayload {
  localConfigPath: string
  localConfigExists: boolean
  telegram: TelegramApiSettings
  globalProxy: GlobalProxySettings
  cloudMail: CloudMailSettings
  ai: AiSettings
  batch: BatchSettings
  sync: SyncSettings
  botAutoSync: BotAutoSyncSettings
  telegramStatus: TelegramStatusAutoRefreshSettings
  logging: LoggingSettings
  timeZone: TimeZoneSettings
  system: SystemInfoSettings
}

export interface TelegramApiSettings {
  apiId: string
  apiHash: string
  profiles?: TelegramApiProfile[] | null
  officialApiEnabled?: boolean | null
  deviceProfiles?: TelegramDeviceProfile[] | null
  defaultDeviceProfileKey?: string | null
  effectiveApiId?: string | null
  effectiveApiSource?: string | null
  effectiveApiName?: string | null
  officialApiId?: string | null
  officialApiName?: string | null
  hasUsableApi?: boolean | null
}

export interface TelegramApiProfile {
  name?: string | null
  apiId?: string | null
  apiHash?: string | null
  enabled: boolean
  weight: number
  notes?: string | null
}

export interface TelegramDeviceProfile {
  key: string
  name: string
  family: string
  appVersion: string
  deviceModel: string
  systemVersion: string
  systemLangCode: string
  langCode: string
  builtIn: boolean
  notes?: string | null
}


export interface GlobalProxySettings {
  enabled: boolean
  sourceMode: GlobalProxySourceMode
  proxyId?: number | null
  proxyName?: string | null
  proxyKind?: ProxyKind | null
  protocol: ProxyProtocol
  server: string
  port: number
  username: string
  hasPassword: boolean
  hasSecret: boolean
}

export interface SaveGlobalProxySettingsRequest {
  enabled: boolean
  sourceMode: GlobalProxySourceMode
  proxyId?: number | null
  protocol: ProxyProtocol
  server: string
  port: number
  username: string
  password: string
  secret: string
  clearPassword: boolean
}

export interface CloudMailSettings {
  baseUrl: string
  domain: string
  token: string
}

export interface AiSettings {
  endpoint: string
  apiKey: string
  defaultModel: string
  presetModels: string[]
  retryCount: number
}

export interface AiTestResult {
  success: boolean
  model?: string | null
  responseText?: string | null
  error?: string | null
}

export interface BatchSettings {
  defaultDelayMs: number
  maxConcurrent: number
  historyRetentionLimit: number
  autoRetry: boolean
  maxRetries: number
}

export interface SyncSettings {
  autoSyncEnabled: boolean
  intervalHours: number
}

export interface BotAutoSyncSettings {
  enabled: boolean
  intervalSeconds: number
}

export interface TelegramStatusAutoRefreshSettings {
  enabled: boolean
  intervalMinutes: number
  batchSize: number
  minAgeMinutes: number
  delayMs: number
}

export interface LoggingSettings {
  enabled: boolean
  level: string
  retentionDays: number
}

export interface BucketBackupSettings {
  enabled: boolean
  uploadUrl: string
  method: 'PUT' | 'POST'
  hasAuthorizationHeader: boolean
  timeoutSeconds: number
}

export interface SaveBucketBackupSettings {
  enabled: boolean
  uploadUrl?: string | null
  method: 'PUT' | 'POST'
  authorizationHeader?: string | null
  clearAuthorizationHeader: boolean
  timeoutSeconds: number
}

export interface BucketBackupResult {
  success: boolean
  message: string
  url?: string | null
  sizeBytes?: number | null
  completedAtUtc: string
}

export interface TimeZoneSettings {
  timeZoneId: string
  effectiveHint?: string | null
}

export interface SystemInfoSettings {
  version: string
  runtime: string
  database: string
  effectiveApiId?: string | null
}

export interface VersionInfo {
  success: boolean
  error?: string | null
  enabled: boolean
  currentVersion: string
  latestVersion?: string | null
  latestTag?: string | null
  updateAvailable: boolean
  url?: string | null
  publishedAt?: string | null
  notes?: string | null
  checkedAtUtc: string
  isDocker: boolean
  updateMode: UpdateMode
  canApply: boolean
  blockedReason?: string | null
  assetName?: string | null
  assetSizeBytes?: number | null
}

export interface VersionApplyResult {
  success: boolean
  message: string
  restartScheduled: boolean
  latestTag?: string | null
  latestVersion?: string | null
}

export type UpdateMode = 'auto' | 'image' | 'binary'

export interface UpdateModeResult {
  success: boolean
  mode: UpdateMode
  message: string
}

export interface ImportResult {
  success: boolean
  phone?: string | null
  userId?: number | null
  username?: string | null
  sessionPath?: string | null
  error?: string | null
  sourceKey?: string | null
  proxyLine?: number | null
  proxyId?: number | null
  proxyName?: string | null
  proxyEgressIp?: string | null
}

export interface ImportAccountsResponse {
  results: ImportResult[]
  accounts: AccountListItem[]
}

export interface AccountLoginResponse {
  success: boolean
  loginId: number
  nextStep?: string | null
  message?: string | null
  account?: AccountListItem | null
}

export interface AccountQrLoginResponse {
  success: boolean
  loginId: number
  status: 'pending' | 'authorized' | 'password' | 'expired' | 'failed' | string
  message?: string | null
  qrLoginUrl?: string | null
  expiresAtUtc?: string | null
  account?: AccountListItem | null
}

export interface CleanupWasteResult {
  deleted: number
  skipped: number
  failed: number
  items: AccountOperationItem[]
}

export interface EmailOperationResult {
  success: boolean
  error?: string | null
  emailPattern?: string | null
}

export interface BatchChangeRecoveryEmailRequest {
  accountIds: number[]
  cloudMailBaseUrl?: string | null
  cloudMailToken?: string | null
  domain?: string | null
  changeLoginEmail: boolean
  trySetLoginEmailWhenMissing: boolean
  useStoredPasswords: boolean
  currentPassword?: string | null
  autoConfirm: boolean
  pollIntervalSeconds: number
  pollTimeoutSeconds: number
  sendEmailFilter?: string | null
  subjectFilter?: string | null
}

export interface TwoFactorRecoveryEmailStatus {
  success: boolean
  error?: string | null
  hasTwoFactorPassword: boolean
  hasRecoveryEmail: boolean
  unconfirmedEmailPattern?: string | null
}

export interface LoginEmailStatus {
  success: boolean
  error?: string | null
  hasLoginEmail: boolean
  loginEmailPattern?: string | null
}

export interface BatchTask {
  id: number
  name?: string | null
  taskType: string
  ownerModuleId: string
  executionKind: 'batch' | 'persistent' | string
  status: string
  total: number
  completed: number
  failed: number
  config?: string | null
  createdAt: string
  startedAt?: string | null
  completedAt?: string | null
  runtimePhase?: string | null
  runtimeMessage?: string | null
  heartbeatAtUtc?: string | null
  requiresAttention: boolean
}

export interface ScheduledTask {
  id: number
  name: string
  taskType: string
  status: string
  total: number
  configJson?: string | null
  cronExpression: string
  nextRunAtUtc?: string | null
  lastRunAtUtc?: string | null
  lastBatchTaskId?: number | null
  createdAt: string
  updatedAt: string
}

export interface TaskDefinition {
  taskType: string
  ownerModuleId: string
  executionKind: 'batch' | 'persistent' | string
  displayName: string
  category: string
  description?: string | null
  icon: string
  createRoute?: string | null
  canCreate: boolean
  canSchedule: boolean
  canPause: boolean
  canResume: boolean
  canEdit: boolean
  canRerun: boolean
  autoPauseBeforeEdit: boolean
}

export interface TaskCenter {
  tasks: BatchTask[]
  scheduledTasks: ScheduledTask[]
  definitions: TaskDefinition[]
  timeZoneId: string
}

export interface ModuleNavItem {
  title: string
  href: string
  icon?: string | null
  group?: string | null
  order: number
  moduleId: string
  pageKey?: string | null
  uiMode?: 'direct' | 'embedded' | 'legacy' | string
}

export interface CreateChatMembershipTaskRequest {
  accountIds: number[]
  operation: 'join' | 'leave'
  links: string[]
  treatNoBotSuffixAsBot?: boolean
  delayMs?: number | null
}

export interface CreateTaskRequest {
  taskType: string
  total: number
  name?: string | null
  config?: string | null
}

export interface CreateScheduledTaskRequest {
  name: string
  taskType: string
  total: number
  configJson?: string | null
  cronExpression: string
  status?: string | null
}

export interface UpdateTaskRequest extends CreateTaskRequest {}

export interface UpdateScheduledTaskRequest extends CreateScheduledTaskRequest {}

export interface TaskAssetUploadResult {
  assetPath: string
  fileName: string
  scopeId: string
}

export interface DataDictionaryItem {
  id: number
  textValue?: string | null
  assetPath?: string | null
  fileName?: string | null
  sortOrder: number
  isEnabled: boolean
  createdAt: string
}

export interface DataDictionary {
  id: number
  name: string
  displayName: string
  description?: string | null
  type: string
  readMode: string
  isEnabled: boolean
  nextIndex: number
  enabledItemCount: number
  createdAt: string
  updatedAt: string
  items: DataDictionaryItem[]
}

export interface SaveTextDictionaryRequest {
  id?: number | null
  name: string
  displayName: string
  description?: string | null
  readMode: string
  isEnabled: boolean
  items: string[]
}

export interface DashboardSummary {
  accountCount: number
  channelCount: number
  groupCount: number
  activeTaskCount: number
  enabledScheduledTaskCount: number
  dictionaryCount: number
  normalAccountCount: number
  limitedAccountCount: number
  invalidAccountCount: number
  activeTasks: BatchTask[]
  recentTasks: BatchTask[]
}

export interface ModuleDependency {
  id: string
  range: string
}

export interface ModuleManifest {
  id: string
  name: string
  version: string
  hostMin?: string | null
  hostMax?: string | null
  dependencies: ModuleDependency[]
  entryAssembly?: string | null
  entryType?: string | null
}

export interface ModuleOverview {
  id: string
  enabled: boolean
  activeVersion?: string | null
  lastGoodVersion?: string | null
  installedVersions: string[]
  manifest?: ModuleManifest | null
  manifestError?: string | null
  builtIn: boolean
}

export interface ModuleCenter {
  modules: ModuleOverview[]
  diagnostics: string[]
}

export interface ModuleOperationResult extends OperationResult {
  moduleId?: string | null
  version?: string | null
}

export interface ExternalApiType {
  type: string
  displayName: string
  route: string
  description?: string | null
  order: number
}

export interface ExternalApiDefinition {
  id: string
  name: string
  type: string
  typeName: string
  route?: string | null
  typeAvailable: boolean
  enabled: boolean
  apiKey: string
  config: Record<string, unknown>
}

export interface ExternalApiCenter {
  apis: ExternalApiDefinition[]
  types: ExternalApiType[]
  availableTypes: string[]
}

export interface SaveExternalApiRequest {
  id?: string | null
  name?: string | null
  type?: string | null
  enabled: boolean
  apiKey?: string | null
  config?: Record<string, unknown> | null
}

export interface BotOption {
  id: number
  name: string
  username?: string | null
  isActive: boolean
}

export interface BotChatOption {
  id: number
  telegramId: number
  title: string
  username?: string | null
  isBroadcast: boolean
  memberCount: number
}

export interface OperationAccount {
  id: number
  displayNumber: number
  displayPhone: string
  nickname?: string | null
  username?: string | null
  isActive: boolean
  categoryId?: number | null
  categoryName?: string | null
}

export interface SimpleCategory {
  id: number
  name: string
  description?: string | null
  itemCount: number
  createdAt: string
}

export interface ChatMembershipAccount {
  accountId: number
  displayPhone?: string | null
  isCreator: boolean
  isAdmin: boolean
  syncedAt: string
}

export interface ChannelListItem {
  id: number
  telegramId: number
  title: string
  username?: string | null
  memberCount: number
  about?: string | null
  creatorAccountId?: number | null
  creatorDisplayPhone?: string | null
  groupId?: number | null
  groupName?: string | null
  createdAt?: string | null
  systemCreatedAtUtc?: string | null
  syncedAt: string
  accounts: ChatMembershipAccount[]
  warning?: string | null
}

export interface GroupListItem {
  id: number
  telegramId: number
  title: string
  username?: string | null
  memberCount: number
  about?: string | null
  creatorAccountId?: number | null
  creatorDisplayPhone?: string | null
  categoryId?: number | null
  categoryName?: string | null
  createdAt?: string | null
  systemCreatedAtUtc?: string | null
  syncedAt: string
  accounts: ChatMembershipAccount[]
  warning?: string | null
}

export interface ChannelDetail {
  channel: ChannelListItem
  accounts: ChatMembershipAccount[]
}

export interface GroupDetail {
  group: GroupListItem
  accounts: ChatMembershipAccount[]
}

export interface ChatAdmin {
  userId: number
  username?: string | null
  displayName: string
  isCreator: boolean
  rank?: string | null
  status?: string | null
  canInviteUsers?: boolean | null
  canPromoteMembers?: boolean | null
  canRestrictMembers?: boolean | null
}

export interface SyncFailure {
  accountId: number
  phone: string
  error: string
}

export interface SyncResult {
  taskId?: number | null
  totalAccounts: number
  processedAccounts: number
  totalChannelsSynced: number
  totalGroupsSynced: number
  failures: SyncFailure[]
  message: string
}

export interface LinkResult {
  link: string
}

export interface BotManagementItem {
  id: number
  name: string
  username?: string | null
  isActive: boolean
  channelCount: number
  createdAt: string
  lastSyncAt?: string | null
}

export interface BotBinding {
  id: number
  name: string
  username?: string | null
}

export interface BotChannelListItem {
  id: number
  telegramId: number
  title: string
  username?: string | null
  isBroadcast: boolean
  memberCount: number
  about?: string | null
  categoryId?: number | null
  categoryName?: string | null
  channelStatusOk?: boolean | null
  channelStatusCheckedAtUtc?: string | null
  channelStatusError?: string | null
  createdAt?: string | null
  syncedAt: string
  boundBots: BotBinding[]
}

export interface BotChannelRemoteInfo {
  telegramId: number
  type: string
  title?: string | null
  username?: string | null
  description?: string | null
  memberCount?: number | null
}

export interface BotChannelDetail {
  channel: BotChannelListItem
  remoteInfo?: BotChannelRemoteInfo | null
}
