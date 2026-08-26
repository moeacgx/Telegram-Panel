<template>
  <div class="direct-messaging-config">
    <el-alert
      v-if="apiFailure"
      :title="apiFailure"
      type="error"
      :closable="false"
      class="mb-3"
    >
      <template #default>
        <el-button link type="primary" @click="loadOptions">重新加载配置</el-button>
      </template>
    </el-alert>

    <template v-if="isLiveTask">
      <el-form-item label="监听账号">
        <el-select v-model="form.listenerAccountId" filterable class="full" :loading="loadingOptions" placeholder="请选择监听账号">
          <el-option v-for="account in accounts" :key="account.id" :label="account.label" :value="account.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="监听群组">
        <el-select
          v-model="form.chats"
          multiple
          filterable
          collapse-tags
          collapse-tags-tooltip
          class="full"
          :loading="loadingGroups"
          :disabled="form.listenerAccountId <= 0 || !!groupsError"
          placeholder="切换监听账号后加载可选群组"
        >
          <el-option v-for="group in groups" :key="group.id" :label="group.label" :value="group.id" />
        </el-select>
        <div class="form-hint no-offset">监听账号切换后会重新加载群组列表，可多选需要监听的群组。</div>
      </el-form-item>
    </template>

    <template v-else-if="isBatchTask">
      <el-form-item label="手工用户名">
        <el-input v-model="form.manualUsernamesText" type="textarea" :rows="5" placeholder="每行一个用户名，可带或不带 @" />
        <div class="form-hint no-offset">会去重后计入任务总数。</div>
      </el-form-item>
      <el-form-item label="文本词典">
        <el-select v-model="form.dictionaryKey" clearable filterable class="full" placeholder="可选，追加词典内全部用户名">
          <el-option
            v-for="dictionary in textDictionaries"
            :key="dictionary.key"
            :label="`${dictionary.label}（${dictionary.count} 条）`"
            :value="dictionary.key"
          />
        </el-select>
        <div class="form-hint no-offset">任务总数 = 去重后的手工用户名数 + 词典可用条目数。</div>
      </el-form-item>
    </template>

    <el-form-item label="发送账号来源">
      <el-radio-group v-model="form.senderSource">
        <el-radio-button value="Category">账号分类</el-radio-button>
        <el-radio-button value="AccountIds">指定账号</el-radio-button>
      </el-radio-group>
    </el-form-item>
    <el-form-item v-if="form.senderSource === 'Category'" label="发送账号分类">
      <el-select v-model="form.senderCategory" clearable class="full" :loading="loadingOptions" placeholder="请选择发送账号分类">
        <el-option v-for="category in categories" :key="category.id" :label="category.label" :value="category.id" />
      </el-select>
    </el-form-item>
    <el-form-item v-else label="指定发送账号">
      <el-select v-model="form.senderAccountIds" multiple filterable collapse-tags collapse-tags-tooltip class="full" :loading="loadingOptions" placeholder="请选择发送账号">
        <el-option v-for="account in accounts" :key="account.id" :label="account.label" :value="account.id" />
      </el-select>
    </el-form-item>
    <el-form-item label="账号选择方式">
      <el-radio-group v-model="form.senderMode">
        <el-radio-button value="Queue">轮询</el-radio-button>
        <el-radio-button value="Random">随机</el-radio-button>
      </el-radio-group>
    </el-form-item>

    <el-form-item label="内容方式">
      <el-radio-group v-model="form.contentAction">
        <el-radio-button value="MessageRules">文本/图片规则</el-radio-button>
        <el-radio-button value="Forward">原生转发</el-radio-button>
        <el-radio-button value="Todo">原生清单</el-radio-button>
      </el-radio-group>
    </el-form-item>

    <template v-if="form.contentAction === 'MessageRules'">
      <div class="message-rule-toolbar">
        <div>
          <strong>文本/图片规则</strong>
          <div class="form-hint no-offset compact">每条规则可发送文本、图片，或两者组合；最多 50 条。</div>
        </div>
        <el-button type="primary" plain size="small" :icon="Plus" :disabled="form.messageRules.length >= 50" @click="addRule">
          添加规则
        </el-button>
      </div>
      <div v-for="(rule, index) in form.messageRules" :key="rule.id" class="message-rule-card">
        <div class="message-rule-card-head">
          <span>规则 {{ index + 1 }}</span>
          <el-button link type="danger" :icon="Delete" :disabled="form.messageRules.length <= 1" @click="removeRule(index)">删除</el-button>
        </div>
        <el-form-item label="文本内容">
          <el-input v-model="rule.text" type="textarea" :rows="3" placeholder="可留空，但每条规则至少要有文字或图片" />
        </el-form-item>
        <el-form-item label="图片">
          <div class="rule-image-actions">
            <el-upload :auto-upload="false" :show-file-list="false" accept="image/*" :disabled="rule.uploading" @change="uploadRuleImage(index, $event)">
            <el-button :icon="Upload" :loading="rule.uploading">{{ rule.assetId ? '更换图片' : '上传图片' }}</el-button>
          </el-upload>
            <el-button v-if="rule.assetId" link type="danger" :icon="Delete" @click="clearRuleImage(rule)">移除图片</el-button>
          </div>
          <div v-if="rule.assetId" class="form-hint no-offset">已上传：{{ rule.fileName || rule.assetId }}</div>
        </el-form-item>
      </div>
    </template>

    <el-form-item v-else-if="form.contentAction === 'Forward'" label="原生转发来源">
      <el-input v-model="form.forwardUrlsText" type="textarea" :rows="5" placeholder="每行一个 Telegram 消息链接" />
      <div class="form-hint no-offset">由 Telegram 原生转发接口发送来源消息。</div>
    </el-form-item>

    <el-form-item v-else label="原生清单">
      <el-input v-model="form.todoText" type="textarea" :rows="5" placeholder="每行一条原生发送内容或目标项" />
      <div class="form-hint no-offset">按原生清单顺序提交给模块执行器。</div>
    </el-form-item>

    <el-row :gutter="12">
      <el-col :xs="24" :sm="8">
        <el-form-item label="去重天数">
          <el-input-number v-model="form.dedupDays" :min="0" :max="3650" class="full" />
        </el-form-item>
      </el-col>
      <el-col :xs="24" :sm="8">
        <el-form-item label="去重范围">
          <el-select v-model="form.dedupeScope" class="full">
            <el-option label="全局" value="Global" />
            <el-option label="当前任务" value="Task" />
          </el-select>
        </el-form-item>
      </el-col>
      <el-col :xs="24" :sm="8">
        <el-form-item label="冷却秒数">
          <el-input-number v-model="form.cooldownSeconds" :min="0" :max="86400" class="full" />
        </el-form-item>
      </el-col>
    </el-row>
    <el-form-item label="滚动24小时上限">
      <el-input-number v-model="form.rolling24h" :min="1" :max="1000" class="full" />
      <div class="form-hint no-offset">范围 1 至 1000 条。</div>
    </el-form-item>

    <el-alert v-if="draft.validationError" :title="draft.validationError" type="warning" :closable="false" class="mt-2" />
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import type { UploadFile } from 'element-plus'
import { Delete, Plus, Upload } from '@element-plus/icons-vue'
import { api, extractApiErrorMessage } from '@/api/client'
import type { TaskConfigDraft } from './TaskConfigForm.vue'

type SenderSource = 'Category' | 'AccountIds'
type SenderMode = 'Queue' | 'Random'
type ContentAction = 'MessageRules' | 'Forward' | 'Todo'
type DedupeScope = 'Global' | 'Task'

const telegramUsernamePattern = /^[A-Za-z][A-Za-z0-9_]{4,31}$/

interface SelectOption {
  id: number
  label: string
}

interface DictionaryOption {
  key: string
  label: string
  count: number
}

interface MessageRuleForm {
  id: string
  text: string
  assetId: string
  fileName: string
  uploading: boolean
  passthrough: Record<string, unknown>
}

interface DirectMessagingForm {
  listenerAccountId: number
  chats: number[]
  manualUsernamesText: string
  dictionaryKey: string
  senderSource: SenderSource
  senderCategory: number | null
  senderAccountIds: number[]
  senderMode: SenderMode
  contentAction: ContentAction
  messageRules: MessageRuleForm[]
  forwardUrlsText: string
  todoText: string
  dedupDays: number
  dedupeScope: DedupeScope
  cooldownSeconds: number
  rolling24h: number
}

const props = defineProps<{
  taskType: string
  initialConfigJson?: string | null
}>()

const emit = defineEmits<{
  'draft-changed': [draft: TaskConfigDraft]
}>()

let ruleSequence = 0
let restoringInitialConfig = false
let groupRequestSequence = 0

const loadingOptions = ref(false)
const loadingGroups = ref(false)
const optionsError = ref('')
const groupsError = ref('')
const assetError = ref('')
const accounts = ref<SelectOption[]>([])
const categories = ref<SelectOption[]>([])
const groups = ref<SelectOption[]>([])
const textDictionaries = ref<DictionaryOption[]>([])
const form = reactive<DirectMessagingForm>(defaultForm())
const draft = reactive<TaskConfigDraft>(invalidDraft('配置加载中'))
const rawInitialConfig = ref<Record<string, unknown>>({})

const isLiveTask = computed(() => props.taskType === 'direct_message.live')
const isBatchTask = computed(() => props.taskType === 'direct_message.batch')
const supportedTaskType = computed(() => isLiveTask.value || isBatchTask.value)
const selectedDictionaryCount = computed(() =>
  textDictionaries.value.find((item) => item.key === form.dictionaryKey)?.count || 0,
)
const apiFailure = computed(() =>
  optionsError.value || (isLiveTask.value ? groupsError.value : '') || assetError.value,
)

onMounted(() => {
  void loadOptions()
})

watch(
  () => [props.taskType, props.initialConfigJson],
  ([taskType], previous) => {
    const taskTypeChanged = !!previous && taskType !== previous[0]
    resetForm()
    if (!taskTypeChanged) applyInitialConfig()
    if (isLiveTask.value && form.listenerAccountId <= 0 && accounts.value.length > 0) {
      form.listenerAccountId = accounts.value[0].id
    }
    pushDraft()
  },
  { immediate: true },
)

watch(form, pushDraft, { deep: true })

watch(
  () => form.listenerAccountId,
  (accountId, previousAccountId) => {
    if (!isLiveTask.value) return
    if (!restoringInitialConfig && accountId !== previousAccountId) form.chats = []
    void loadGroups(accountId)
  },
  { flush: 'sync' },
)

async function loadOptions() {
  loadingOptions.value = true
  optionsError.value = ''
  try {
    const response = await api.get<unknown>('/extensions/direct-messaging/options')
    const payload = response.data
    accounts.value = normalizeSelectOptions(findArray(payload, ['accounts', 'operationAccounts', 'senderAccounts']), ['displayPhone', 'phone', 'nickname', 'username', 'name'])
    categories.value = normalizeSelectOptions(findArray(payload, ['accountCategories', 'categories', 'senderCategories']), ['name', 'displayName'])
    textDictionaries.value = normalizeDictionaryOptions(findArray(payload, ['textDictionaries', 'dictionaries', 'usernameDictionaries']))
    if (isLiveTask.value && form.listenerAccountId <= 0 && accounts.value.length > 0) {
      form.listenerAccountId = accounts.value[0].id
    }
  } catch (error) {
    optionsError.value = requestErrorMessage(error, '无法加载私信任务配置选项')
  } finally {
    loadingOptions.value = false
    pushDraft()
  }
}

async function loadGroups(accountId: number) {
  const requestId = ++groupRequestSequence
  groupsError.value = ''
  if (accountId <= 0) {
    groups.value = []
    loadingGroups.value = false
    pushDraft()
    return
  }

  loadingGroups.value = true
  try {
    const response = await api.get<unknown>(`/extensions/direct-messaging/accounts/${encodeURIComponent(String(accountId))}/groups`)
    if (requestId !== groupRequestSequence) return
    groups.value = normalizeSelectOptions(findArray(response.data, ['groups', 'items', 'data']), ['title', 'name', 'username'])
  } catch (error) {
    if (requestId !== groupRequestSequence) return
    groups.value = []
    groupsError.value = requestErrorMessage(error, '无法加载监听账号的群组列表')
  } finally {
    if (requestId === groupRequestSequence) {
      loadingGroups.value = false
      pushDraft()
    }
  }
}

function resetForm() {
  Object.assign(form, defaultForm())
  rawInitialConfig.value = {}
  groups.value = []
  groupsError.value = ''
  assetError.value = ''
}

function applyInitialConfig() {
  const raw = (props.initialConfigJson || '').trim()
  if (!raw) return

  let config: Record<string, unknown>
  try {
    config = JSON.parse(raw) as Record<string, unknown>
  } catch {
    return
  }

  rawInitialConfig.value = cloneRecord(config)

  restoringInitialConfig = true
  try {
    const content = asRecord(readValue(config, 'content'))
    form.listenerAccountId = positiveNumber(readValue(config, 'listenerAccountId', 'monitor_account_id', 'monitorAccountId', 'listener_account_id'), 0)
    form.chats = numberList(readValue(config, 'chats', 'group_ids', 'groupIds', 'target_group_ids'))
    form.manualUsernamesText = stringList(readValue(config, 'usernames', 'manual_usernames', 'manualUsernames')).join('\n')
    form.dictionaryKey = textValue(readValue(config, 'dictionaryKey', 'text_dictionary', 'textDictionary', 'dictionary'))
    form.senderSource = normalizeSenderSource(readValue(config, 'senderSource', 'sender_source_mode', 'senderSourceMode', 'account_source_mode'))
    form.senderCategory = nullablePositiveNumber(readValue(config, 'senderCategory', 'sender_category') ?? readValue(config, 'sender_category_ids', 'senderCategoryIds', 'category_ids'))
    form.senderAccountIds = numberList(readValue(config, 'senderAccountIds', 'sender_account_ids', 'account_ids'))
    form.senderMode = normalizeSenderMode(readValue(config, 'senderMode', 'sender_selection_mode', 'senderSelectionMode', 'account_mode'))
    form.contentAction = normalizeContentAction((content ? readValue(content, 'action') : undefined) ?? readValue(config, 'content_mode', 'contentMode', 'message_action_mode'))
    form.messageRules = readMessageRules((content ? readValue(content, 'messageRules', 'rules') : undefined) ?? readValue(config, 'message_rules', 'messageRules'))
    form.forwardUrlsText = stringList((content ? readValue(content, 'forward', 'forwardUrls', 'forwardSourceUrls') : undefined) ?? readValue(config, 'forward_source_urls', 'forwardSourceUrls', 'forward_urls')).join('\n')
    form.todoText = stringList((content ? readValue(content, 'todo', 'todoItems', 'items') : undefined) ?? readValue(config, 'native_list', 'nativeList', 'native_items')).join('\n')
    form.dedupDays = clampInteger(readValue(config, 'dedupeDays', 'dedup_days', 'dedupDays'), 30, 0, 3650)
    form.dedupeScope = normalizeDedupeScope(readValue(config, 'dedupeScope', 'dedup_scope', 'dedupScope'))
    form.cooldownSeconds = clampInteger(readValue(config, 'cooldownSeconds', 'cooldown_seconds'), 60, 0, 86400)
    form.rolling24h = clampInteger(readValue(config, 'rolling24h', 'max_messages_per_24_hours', 'maxMessagesPer24Hours', 'rolling_24h_limit'), 20, 1, 1000)
  } finally {
    restoringInitialConfig = false
  }
}

function pushDraft() {
  let next: TaskConfigDraft
  try {
    next = buildDraft()
  } catch (error) {
    next = invalidDraft(error instanceof Error ? error.message : '私信任务配置无效')
  }
  Object.assign(draft, next)
  emit('draft-changed', { ...next })
}

function buildDraft(): TaskConfigDraft {
  if (!supportedTaskType.value) return invalidDraft('该任务类型没有私信专用配置表单')
  if (loadingOptions.value) return invalidDraft('正在加载私信任务配置选项')
  if (apiFailure.value) return invalidDraft(apiFailure.value)
  if (form.messageRules.some((rule) => rule.uploading)) return invalidDraft('图片上传中，请等待完成')

  const senderCategory = nullablePositiveNumber(form.senderCategory)
  const senderAccountIds = uniqueNumbers(form.senderAccountIds)
  if (form.senderSource === 'Category' && !senderCategory) {
    return invalidDraft('请选择发送账号分类')
  }
  if (form.senderSource === 'AccountIds' && senderAccountIds.length === 0) {
    return invalidDraft('请至少选择一个发送账号')
  }

  const chats = uniqueNumbers(form.chats)
  if (isLiveTask.value && form.listenerAccountId <= 0) return invalidDraft('请选择监听账号')
  if (isLiveTask.value && loadingGroups.value) return invalidDraft('正在加载监听账号的群组列表')
  if (isLiveTask.value && chats.length === 0) return invalidDraft('请至少选择一个监听群组')

  const parsedUsernames = parseUsernames(form.manualUsernamesText)
  if (isBatchTask.value && parsedUsernames.invalid.length > 0) {
    return invalidDraft(`用户名格式无效：${parsedUsernames.invalid.slice(0, 3).join('、')}`)
  }
  const usernames = parsedUsernames.usernames
  if (isBatchTask.value && usernames.length > 10000) return invalidDraft('手工用户名最多 10000 条')
  const dictionaryCount = selectedDictionaryCount.value
  if (isBatchTask.value && form.dictionaryKey && dictionaryCount <= 0) {
    return invalidDraft('请选择包含可用条目的文本词典')
  }
  if (isBatchTask.value && usernames.length + dictionaryCount === 0) {
    return invalidDraft('请填写手工用户名或选择文本词典')
  }

  const messageRules = normalizeRules(form.messageRules)
  const forward = uniqueLines(form.forwardUrlsText)
  const todo = uniqueLines(form.todoText)
  if (form.contentAction === 'MessageRules' && (form.messageRules.length < 1 || form.messageRules.length > 50 || messageRules.length === 0)) {
    return invalidDraft('请至少填写一条文本/图片规则')
  }
  if (form.contentAction === 'Forward' && forward.length === 0) {
    return invalidDraft('请至少填写一个原生转发来源')
  }
  if (form.contentAction === 'Todo' && todo.length === 0) {
    return invalidDraft('请至少填写一条原生清单内容')
  }

  const dedupeDays = clampInteger(form.dedupDays, 30, 0, 3650)
  const cooldownSeconds = clampInteger(form.cooldownSeconds, 60, 0, 86400)
  const rolling24h = clampInteger(form.rolling24h, 20, 1, 1000)
  const rawConfig = cloneRecord(rawInitialConfig.value)
  const rawContent = cloneRecord(asRecord(rawConfig.content))
  const rawMessageRules = readValue(rawContent, 'messageRules', 'rules')
  const rawForward = readValue(rawContent, 'forward', 'forwardUrls', 'forwardSourceUrls')
  const rawTodo = readValue(rawContent, 'todo', 'todoItems', 'items')
  removeManagedTopLevelFields(rawConfig)
  removeManagedContentFields(rawContent)

  const content = {
    ...rawContent,
    action: form.contentAction,
    messageRules: form.contentAction === 'MessageRules' ? messageRules : cloneContentItems(rawMessageRules),
    forward: form.contentAction === 'Forward' ? mergeContentItems(rawForward, forward, ['url', 'sourceUrl', 'value', 'text', 'content']) : cloneContentItems(rawForward),
    todo: form.contentAction === 'Todo' ? mergeContentItems(rawTodo, todo, ['text', 'content', 'value', 'item']) : cloneContentItems(rawTodo),
  }

  const config = {
    ...rawConfig,
    listenerAccountId: isLiveTask.value ? form.listenerAccountId : null,
    chats: isLiveTask.value ? chats : [],
    usernames: isBatchTask.value ? usernames : [],
    dictionaryKey: isBatchTask.value ? form.dictionaryKey || null : null,
    senderSource: form.senderSource,
    senderCategory: form.senderSource === 'Category' ? senderCategory : null,
    senderAccountIds: form.senderSource === 'AccountIds' ? senderAccountIds : [],
    senderMode: form.senderMode,
    dedupeDays,
    dedupeScope: form.dedupeScope,
    cooldownSeconds,
    rolling24h,
    content,
  }

  return validDraft(isBatchTask.value ? usernames.length + dictionaryCount : 0, config)
}

function addRule() {
  if (form.messageRules.length < 50) form.messageRules.push(newRule())
}

function removeRule(index: number) {
  if (form.messageRules.length > 1) form.messageRules.splice(index, 1)
}

function clearRuleImage(rule: MessageRuleForm) {
  rule.assetId = ''
  rule.fileName = ''
  assetError.value = ''
}

async function uploadRuleImage(index: number, file: UploadFile) {
  const raw = file.raw
  const rule = form.messageRules[index]
  if (!raw || !rule) return

  rule.uploading = true
  assetError.value = ''
  try {
    const formData = new FormData()
    formData.append('file', raw)
    const response = await api.post<unknown>('/extensions/direct-messaging/assets', formData)
    const payload = asRecord(response.data)
    const asset = asRecord(payload ? readValue(payload, 'asset') : undefined)
    const assetId = textValue((asset ? readValue(asset, 'assetId', 'id') : undefined) ?? (payload ? readValue(payload, 'assetId', 'id') : undefined))
    if (!assetId) throw new Error('图片上传接口没有返回 assetId')
    rule.assetId = assetId
    rule.fileName = textValue((asset ? readValue(asset, 'fileName', 'name') : undefined) ?? (payload ? readValue(payload, 'fileName', 'name') : undefined)) || raw.name
  } catch (error) {
    assetError.value = requestErrorMessage(error, '图片上传失败')
  } finally {
    rule.uploading = false
    pushDraft()
  }
}

function defaultForm(): DirectMessagingForm {
  return {
    listenerAccountId: 0,
    chats: [],
    manualUsernamesText: '',
    dictionaryKey: '',
    senderSource: 'Category',
    senderCategory: null,
    senderAccountIds: [],
    senderMode: 'Queue',
    contentAction: 'MessageRules',
    messageRules: [newRule()],
    forwardUrlsText: '',
    todoText: '',
    dedupDays: 30,
    dedupeScope: 'Global',
    cooldownSeconds: 60,
    rolling24h: 20,
  }
}

function newRule(): MessageRuleForm {
  ruleSequence += 1
  return { id: `direct-rule-${ruleSequence}`, text: '', assetId: '', fileName: '', uploading: false, passthrough: {} }
}

function validDraft(total: number, config: Record<string, unknown>): TaskConfigDraft {
  return { total: Math.max(0, total), config: JSON.stringify(config), canSubmit: true, validationError: null }
}

function invalidDraft(validationError: string): TaskConfigDraft {
  return { total: 0, config: null, canSubmit: false, validationError }
}

function removeManagedTopLevelFields(config: Record<string, unknown>) {
  for (const key of [
    'listenerAccountId', 'chats', 'usernames', 'dictionaryKey', 'senderSource', 'senderCategory', 'senderAccountIds', 'senderMode',
    'dedupeDays', 'dedupeScope', 'cooldownSeconds', 'rolling24h', 'content',
    'monitor_account_id', 'monitorAccountId', 'listener_account_id', 'group_ids', 'groupIds', 'target_group_ids',
    'manual_usernames', 'manualUsernames', 'text_dictionary', 'textDictionary', 'dictionary',
    'sender_source_mode', 'senderSourceMode', 'account_source_mode', 'sender_category', 'sender_category_ids', 'senderCategoryIds', 'category_ids',
    'sender_account_ids', 'account_ids', 'sender_selection_mode', 'senderSelectionMode', 'account_mode',
    'content_mode', 'contentMode', 'message_action_mode', 'message_rules', 'forward_source_urls', 'forwardSourceUrls', 'forward_urls',
    'native_list', 'nativeList', 'native_items', 'dedup_days', 'dedupScope', 'dedup_scope', 'cooldown_seconds',
    'max_messages_per_24_hours', 'maxMessagesPer24Hours', 'rolling_24h_limit',
  ]) {
    delete config[key]
  }
}

function removeManagedContentFields(content: Record<string, unknown>) {
  for (const key of ['action', 'messageRules', 'rules', 'forward', 'forwardUrls', 'forwardSourceUrls', 'todo', 'todoItems', 'items']) {
    delete content[key]
  }
}

function extractRulePassthrough(record: Record<string, unknown>): Record<string, unknown> {
  const passthrough = cloneRecord(record)
  for (const key of ['text', 'content', 'assetId', 'imageAssetId', 'image_asset_path', 'imageAssetPath', 'image_path', 'imagePath', 'fileName', 'imageName', 'image_name']) {
    delete passthrough[key]
  }
  return passthrough
}

function mergeContentItems(rawValue: unknown, values: string[], valueKeys: string[]): unknown[] {
  const rawItems = Array.isArray(rawValue) ? rawValue : []
  const merged = values.map((value, index) => {
    const rawItem = asRecord(rawItems[index])
    if (!rawItem) return value
    const item = cloneRecord(rawItem)
    const valueKey = valueKeys.find((key) => typeof item[key] === 'string') || valueKeys[0]
    item[valueKey] = value
    return item
  })

  for (const rawItem of rawItems.slice(values.length)) {
    if (isUnrecognizedContentItem(rawItem, valueKeys)) merged.push(cloneValue(rawItem))
  }
  return merged
}

function cloneContentItems(value: unknown): unknown[] {
  return Array.isArray(value) ? cloneValue(value) : []
}

function isUnrecognizedContentItem(value: unknown, valueKeys: string[]) {
  const record = asRecord(value)
  if (!record) return typeof value !== 'string'
  return !valueKeys.some((key) => textValue(record[key]))
}

function cloneRecord(value: Record<string, unknown> | null | undefined): Record<string, unknown> {
  return value ? cloneValue(value) : {}
}

function cloneValue<T>(value: T): T {
  return JSON.parse(JSON.stringify(value)) as T
}

function findArray(value: unknown, keys: string[]): unknown[] {
  if (Array.isArray(value)) return value
  const record = asRecord(value)
  if (!record) return []
  for (const key of keys) {
    if (Array.isArray(record[key])) return record[key] as unknown[]
  }
  const nested = asRecord(record.data)
  if (!nested) return []
  for (const key of keys) {
    if (Array.isArray(nested[key])) return nested[key] as unknown[]
  }
  return []
}

function normalizeSelectOptions(items: unknown[], labelKeys: string[]): SelectOption[] {
  const seen = new Set<number>()
  return items.flatMap((item) => {
    const record = asRecord(item)
    if (!record) return []
    const id = positiveNumber(readValue(record, 'id', 'accountId', 'groupId', 'telegramId'), 0)
    if (id <= 0 || seen.has(id)) return []
    seen.add(id)
    const labelParts = labelKeys.map((key) => textValue(record[key])).filter(Boolean)
    return [{ id, label: labelParts.length > 0 ? labelParts.join(' / ') : `#${id}` }]
  })
}

function normalizeDictionaryOptions(items: unknown[]): DictionaryOption[] {
  const seen = new Set<string>()
  return items.flatMap((item) => {
    const record = asRecord(item)
    if (!record) return []
    const key = textValue(readValue(record, 'key', 'dictionaryKey', 'name', 'dictionaryName', 'id'))
    if (!key || seen.has(key)) return []
    seen.add(key)
    const label = textValue(readValue(record, 'displayName', 'label', 'name')) || key
    const count = Math.max(0, positiveNumber(readValue(record, 'enabledItemCount', 'count', 'itemCount'), 0))
    return [{ key, label, count }]
  })
}

function normalizeRules(value: MessageRuleForm[]) {
  return value
    .map((rule) => {
      const normalized: Record<string, unknown> & { text: string; assetId: string | null } = {
        ...cloneRecord(rule.passthrough),
        text: rule.text.trim(),
        assetId: rule.assetId.trim() || null,
      }
      if (rule.fileName.trim()) normalized.fileName = rule.fileName.trim()
      return normalized
    })
    .filter((rule, index) => rule.text || rule.assetId || Object.keys(value[index].passthrough).length > 0)
}

function readMessageRules(value: unknown): MessageRuleForm[] {
  if (!Array.isArray(value)) return [newRule()]
  const rules = value.slice(0, 50).flatMap((item) => {
    const record = asRecord(item)
    if (!record) return []
    return [{
      ...newRule(),
      text: textValue(readValue(record, 'text', 'content')),
      assetId: textValue(readValue(record, 'assetId', 'imageAssetId', 'image_asset_path', 'imageAssetPath', 'image_path', 'imagePath')),
      fileName: textValue(readValue(record, 'fileName', 'imageName', 'image_name')),
      passthrough: extractRulePassthrough(record),
    }]
  })
  return rules.length > 0 ? rules : [newRule()]
}

function parseUsernames(value: string): { usernames: string[]; invalid: string[] } {
  const seen = new Set<string>()
  const usernames: string[] = []
  const invalid: string[] = []
  for (const item of uniqueLines(value)) {
    const username = item.replace(/^@+/, '').trim()
    const key = username.toLocaleLowerCase()
    if (!username || seen.has(key)) continue
    seen.add(key)
    if (!telegramUsernamePattern.test(username)) {
      invalid.push(username)
      continue
    }
    usernames.push(username)
  }
  return { usernames, invalid }
}

function uniqueLines(value: string): string[] {
  return Array.from(new Set(value.split(/[\r\n]+/).map((item) => item.trim()).filter(Boolean)))
}

function uniqueNumbers(value: number[]): number[] {
  return Array.from(new Set(value.map((item) => Math.trunc(Number(item))).filter((item) => Number.isFinite(item) && item > 0)))
}

function numberList(value: unknown): number[] {
  if (!Array.isArray(value)) return []
  return uniqueNumbers(value.map((item) => positiveNumber(item, 0)))
}

function nullablePositiveNumber(value: unknown): number | null {
  const candidate = Array.isArray(value) ? value[0] : value
  const number = Math.trunc(positiveNumber(candidate, 0))
  return number > 0 ? number : null
}

function stringList(value: unknown): string[] {
  if (Array.isArray(value)) return value.flatMap((item) => stringList(item))
  if (typeof value === 'string') return uniqueLines(value)
  const record = asRecord(value)
  if (record) return stringList(readValue(record, 'url', 'sourceUrl', 'value', 'text', 'content', 'item'))
  return []
}

function readValue(record: Record<string, unknown>, ...keys: string[]): unknown {
  for (const key of keys) {
    if (record[key] !== undefined && record[key] !== null) return record[key]
  }
  return undefined
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return value && typeof value === 'object' && !Array.isArray(value) ? value as Record<string, unknown> : null
}

function textValue(value: unknown): string {
  return typeof value === 'string' ? value.trim() : typeof value === 'number' ? String(value) : ''
}

function positiveNumber(value: unknown, fallback: number): number {
  const number = Number(value)
  return Number.isFinite(number) ? number : fallback
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(max, Math.max(min, value))
}

function clampInteger(value: unknown, fallback: number, min: number, max: number): number {
  return clamp(Math.trunc(positiveNumber(value, fallback)), min, max)
}

function normalizeSenderSource(value: unknown): SenderSource {
  return value === 'AccountIds' || value === 'account' || value === 'specified' ? 'AccountIds' : 'Category'
}

function normalizeSenderMode(value: unknown): SenderMode {
  return value === 'Random' || value === 'random' ? 'Random' : 'Queue'
}

function normalizeContentAction(value: unknown): ContentAction {
  if (value === 'Forward' || value === 'native_forward' || value === 'forward' || value === 'forward_url') return 'Forward'
  if (value === 'Todo' || value === 'native_list' || value === 'list') return 'Todo'
  return 'MessageRules'
}

function normalizeDedupeScope(value: unknown): DedupeScope {
  return value === 'Task' || value === 'task' || value === 'target' || value === 'sender' ? 'Task' : 'Global'
}

function requestErrorMessage(error: unknown, fallback: string): string {
  const response = asRecord(error)
  const responseData = asRecord(response?.response)
  const data = responseData?.data
  return extractApiErrorMessage(data) || (error instanceof Error && error.message) || fallback
}
</script>

<style scoped>
.direct-messaging-config {
  width: 100%;
}

.full {
  width: 100%;
}

.message-rule-toolbar,
.message-rule-card-head,
.rule-image-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.message-rule-toolbar,
.message-rule-card-head {
  justify-content: space-between;
}

.message-rule-toolbar {
  margin: 4px 0 12px;
}

.message-rule-card {
  margin-bottom: 12px;
  padding: 12px;
  border: 1px solid var(--el-border-color);
  border-radius: 4px;
}

.message-rule-card-head {
  margin-bottom: 8px;
  font-size: 14px;
  font-weight: 600;
}

.form-hint {
  margin-top: 6px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

.no-offset {
  width: 100%;
}

.compact {
  margin-top: 2px;
}

@media (max-width: 640px) {
  .message-rule-toolbar {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
