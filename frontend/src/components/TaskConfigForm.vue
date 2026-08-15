<template>
  <div>
    <el-alert v-if="loading" title="正在加载配置项..." type="info" :closable="false" class="mb-3" />

    <template v-if="taskType === 'user_chat_active'">
      <el-alert
        title="MaxMessages=0 表示持续运行，直到在任务中心手动取消；MaxMessages>0 时每次运行最多按可用账号数发送，每个账号最多 1 条。目标支持群组、频道和 Bot 用户名/链接；每条消息规则支持多段文字、图片字典、{time} 和文本字典变量。"
        type="info"
        :closable="false"
        class="mb-3"
      />
      <el-form-item label="账号分类">
        <el-select v-model="forms.userChatActive.categoryIds" multiple collapse-tags collapse-tags-tooltip class="full" placeholder="请选择执行账号分类">
          <el-option v-for="item in accountCategories" :key="item.id" :label="categoryLabel(item)" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="目标">
        <el-input v-model="forms.userChatActive.targetsText" type="textarea" :rows="5" placeholder="每行一个群组/频道/Bot 链接、用户名或 ID" />
      </el-form-item>
      <div class="message-rule-section">
        <div class="message-rule-toolbar">
          <div>
            <strong>消息规则</strong>
            <div class="form-hint no-offset compact">规则按「消息规则模式」随机或队列循环；每条规则可以是多段文字、图片字典，或图片 + 说明文字。</div>
          </div>
          <el-button type="primary" plain size="small" @click="addUserChatActiveRule()">添加规则</el-button>
        </div>

        <div v-for="(rule, index) in forms.userChatActive.messageRules" :key="rule.id" class="message-rule-card">
          <div class="message-rule-card-head">
            <span>规则 {{ index + 1 }}</span>
            <el-button link type="danger" :disabled="forms.userChatActive.messageRules.length <= 1" @click="removeUserChatActiveRule(index)">删除</el-button>
          </div>
          <el-form-item label="消息内容">
            <el-input v-model="rule.text" type="textarea" :rows="4" placeholder="可写多行段落，支持 {time} 和文本字典变量；留空时可只发送图片。" />
          </el-form-item>
          <el-form-item label="图片字典">
            <el-select v-model="rule.imageDictionaryName" class="full" placeholder="不发送图片">
              <el-option label="不发送图片" value="" />
              <el-option v-for="name in imageDictionaryNames" :key="name" :label="name" :value="name" />
            </el-select>
          </el-form-item>
        </div>

        <el-form-item label="批量追加">
          <el-input v-model="forms.userChatActive.bulkRulesText" type="textarea" :rows="3" placeholder="一行一条文字消息；点击追加后会生成多条规则。需要段落格式时，直接在上方规则卡片里写多行。" />
        </el-form-item>
        <div class="form-hint">
          <el-button size="small" @click="appendUserChatActiveLineRules">按行追加为规则</el-button>
          可用文本变量：{{ textVariableHint }}
        </div>
      </div>

      <el-row :gutter="12">
        <el-col :span="8">
          <el-form-item label="最小间隔">
            <el-input-number v-model="forms.userChatActive.delayMinSeconds" :min="0" :max="600" :precision="2" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="最大间隔">
            <el-input-number v-model="forms.userChatActive.delayMaxSeconds" :min="0" :max="600" :precision="2" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="最多条数">
            <el-input-number v-model="forms.userChatActive.maxMessages" :min="0" :max="1000000" class="full" />
          </el-form-item>
        </el-col>
      </el-row>

      <el-row :gutter="12">
        <el-col :span="8">
          <el-form-item label="账号模式">
            <el-select v-model="forms.userChatActive.accountMode" class="full">
              <el-option label="随机" value="random" />
              <el-option label="队列循环" value="queue" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="目标模式">
            <el-select v-model="forms.userChatActive.targetMode" class="full">
              <el-option label="随机" value="random" />
              <el-option label="队列循环" value="queue" />
            </el-select>
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="消息规则模式">
            <el-select v-model="forms.userChatActive.messageMode" class="full">
              <el-option label="随机" value="random" />
              <el-option label="队列循环" value="queue" />
            </el-select>
          </el-form-item>
        </el-col>
      </el-row>

      <el-form-item label="AI 验证">
        <el-switch v-model="forms.userChatActive.enableAiVerification" active-text="启用" inactive-text="关闭" />
      </el-form-item>
      <template v-if="forms.userChatActive.enableAiVerification">
        <el-row :gutter="12">
          <el-col :span="12">
            <el-form-item label="任务模型">
              <el-select v-model="forms.userChatActive.selectedAiModelOption" class="full">
                <el-option :label="globalAiModelLabel" value="__global__" />
                <el-option v-for="model in selectableAiModels" :key="model" :label="model" :value="model" />
                <el-option label="自定义模型" value="__custom__" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :span="12">
            <el-form-item label="超时秒数">
              <el-input-number v-model="forms.userChatActive.verificationTimeoutSeconds" :min="3" :max="300" class="full" />
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item v-if="forms.userChatActive.selectedAiModelOption === '__custom__'" label="模型名">
          <el-input v-model="forms.userChatActive.customAiModel" placeholder="例如 gpt-4o-mini、qwen-vl-plus" />
        </el-form-item>
        <el-form-item label="匹配方式">
          <el-radio-group v-model="forms.userChatActive.verificationMatchMode">
            <el-radio-button label="mention_or_reply">仅 @账号 / 回复</el-radio-button>
            <el-radio-button label="keyword">关键词</el-radio-button>
            <el-radio-button label="regex">正则</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item label="指定机器人">
          <el-switch v-model="forms.userChatActive.verificationBotUsernameFilterEnabled" active-text="启用" inactive-text="关闭" />
        </el-form-item>
        <el-form-item v-if="forms.userChatActive.verificationBotUsernameFilterEnabled" label="机器人名单">
          <el-input v-model="forms.userChatActive.verificationBotUsernamesText" type="textarea" :rows="4" placeholder="每行一个用户名或 ID" />
        </el-form-item>
        <el-form-item v-if="forms.userChatActive.verificationMatchMode === 'keyword'" label="关键词">
          <el-input v-model="forms.userChatActive.verificationKeywordsText" type="textarea" :rows="4" placeholder="每行一个关键词" />
        </el-form-item>
        <el-form-item v-if="forms.userChatActive.verificationMatchMode === 'regex'" label="正则">
          <el-input v-model="forms.userChatActive.verificationRegexText" type="textarea" :rows="4" placeholder="每行一个正则" />
        </el-form-item>
        <el-form-item label="超时失败">
          <el-switch v-model="forms.userChatActive.verificationTimeoutAsFailure" active-text="计入失败" inactive-text="仅记录" />
        </el-form-item>
      </template>
    </template>

    <template v-else-if="taskType === 'channel_group_private_create'">
      <el-form-item label="账号分类">
        <el-select v-model="forms.privateCreate.categoryIds" multiple collapse-tags collapse-tags-tooltip class="full" placeholder="请选择执行账号分类">
          <el-option v-for="item in accountCategories" :key="item.id" :label="categoryLabel(item)" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="创建对象">
        <el-radio-group v-model="forms.privateCreate.createType">
          <el-radio-button label="channel">频道</el-radio-button>
          <el-radio-button label="group">群组</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item v-if="forms.privateCreate.createType === 'channel'" label="频道分组">
        <el-select v-model="forms.privateCreate.channelGroupId" class="full">
          <el-option label="未分组" :value="0" />
          <el-option v-for="item in channelGroups" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item v-else label="群组分类">
        <el-select v-model="forms.privateCreate.groupCategoryId" class="full">
          <el-option label="未分类" :value="0" />
          <el-option v-for="item in groupCategories" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>

      <el-row :gutter="12">
        <el-col :span="12">
          <el-form-item label="每账号累计创建上限">
            <el-input-number v-model="forms.privateCreate.systemCreatedLimit" :min="1" :max="100000" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="本轮每账号创建数">
            <el-input-number v-model="forms.privateCreate.perAccountBatchSize" :min="1" :max="1000" class="full" />
          </el-form-item>
        </el-col>
      </el-row>
      <div class="form-hint">
        每账号累计创建上限：统计该账号已由系统任务创建并记录的频道/群组数量，达到上限后本轮不会继续创建。
        本轮每账号创建数：本次任务里每个账号最多创建几个。比如上限 10、某账号已有 9 个、本轮每账号创建数填 5，
        该账号本轮最多也只会再创建 1 个。
      </div>

      <el-form-item label="标题模板">
        <el-input v-model="forms.privateCreate.titleTemplate" placeholder="支持 {time} 和文本字典变量，例如：临时频道{time}" />
      </el-form-item>
      <div class="form-hint">可用文本变量：{{ textVariableHint }}</div>

      <AvatarFields
        :avatar-source="forms.privateCreate.avatarSource"
        :fixed-avatar-asset-path="forms.privateCreate.fixedAvatarAssetPath"
        :avatar-dictionary-name="forms.privateCreate.avatarDictionaryName"
        :image-dictionaries="imageDictionaryNames"
        :uploading="forms.privateCreate.uploadingAvatar"
        @update:avatar-source="forms.privateCreate.avatarSource = $event"
        @update:avatar-dictionary-name="forms.privateCreate.avatarDictionaryName = $event"
        @upload="uploadAvatar('privateCreate', $event)"
      />

      <DelayFields
        v-model:min-delay="forms.privateCreate.minDelaySeconds"
        v-model:max-delay="forms.privateCreate.maxDelaySeconds"
        v-model:jitter="forms.privateCreate.jitterPercent"
      />
    </template>

    <template v-else-if="taskType === 'channel_group_publicize'">
      <el-form-item label="账号分类">
        <el-select v-model="forms.publicize.categoryIds" multiple collapse-tags collapse-tags-tooltip class="full" placeholder="请选择执行账号分类">
          <el-option v-for="item in accountCategories" :key="item.id" :label="categoryLabel(item)" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="处理对象">
        <el-radio-group v-model="forms.publicize.targetType">
          <el-radio-button label="channel">频道</el-radio-button>
          <el-radio-button label="group">群组</el-radio-button>
        </el-radio-group>
      </el-form-item>
      <el-form-item v-if="forms.publicize.targetType === 'channel'" label="来源分组">
        <el-select v-model="forms.publicize.channelGroupId" class="full">
          <el-option label="未分组" :value="0" />
          <el-option v-for="item in channelGroups" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item v-else label="来源分类">
        <el-select v-model="forms.publicize.groupCategoryId" class="full">
          <el-option label="未分类" :value="0" />
          <el-option v-for="item in groupCategories" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item v-if="forms.publicize.targetType === 'channel'" label="公开后分组">
        <el-select v-model="forms.publicize.targetChannelGroupId" class="full">
          <el-option label="保持原分组" :value="-1" />
          <el-option label="移动到未分组" :value="0" />
          <el-option v-for="item in channelGroups" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item v-else label="公开后分类">
        <el-select v-model="forms.publicize.targetGroupCategoryId" class="full">
          <el-option label="保持原分类" :value="-1" />
          <el-option label="移动到未分类" :value="0" />
          <el-option v-for="item in groupCategories" :key="item.id" :label="item.name" :value="item.id" />
        </el-select>
      </el-form-item>

      <el-row :gutter="12">
        <el-col :span="8">
          <el-form-item label="私密创建满天数">
            <el-input-number v-model="forms.publicize.minSystemCreatedDays" :min="0" :max="3650" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="每账号公开保有上限">
            <el-input-number v-model="forms.publicize.maxPublicCount" :min="1" :max="1000" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="本轮每账号处理数">
            <el-input-number v-model="forms.publicize.perAccountBatchSize" :min="1" :max="1000" class="full" />
          </el-form-item>
        </el-col>
      </el-row>
      <div class="form-hint">
        私密创建满天数：只公开系统记录为私密创建、且已创建达到该天数以上的频道/群组。
        这个限制用于避免刚创建的私密资源太快公开，降低风控和封禁风险。
        公开后分组/分类默认保持原值，也可以在公开成功后自动移动到指定分组。
      </div>

      <el-form-item label="标题模板">
        <el-input v-model="forms.publicize.titleTemplate" placeholder="支持 {time} 和文本字典变量" />
      </el-form-item>
      <el-form-item label="描述模板">
        <el-input v-model="forms.publicize.descriptionTemplate" type="textarea" :rows="3" placeholder="可留空，留空则保持原描述不变" />
      </el-form-item>
      <el-form-item label="公开用户名">
        <el-input v-model="forms.publicize.usernameTemplate" placeholder="支持变量，最终结果会按 Telegram 用户名规则校验" />
      </el-form-item>
      <div class="form-hint">可用文本变量：{{ textVariableHint }}</div>

      <AvatarFields
        :avatar-source="forms.publicize.avatarSource"
        :fixed-avatar-asset-path="forms.publicize.fixedAvatarAssetPath"
        :avatar-dictionary-name="forms.publicize.avatarDictionaryName"
        :image-dictionaries="imageDictionaryNames"
        :uploading="forms.publicize.uploadingAvatar"
        @update:avatar-source="forms.publicize.avatarSource = $event"
        @update:avatar-dictionary-name="forms.publicize.avatarDictionaryName = $event"
        @upload="uploadAvatar('publicize', $event)"
      />

      <DelayFields
        v-model:min-delay="forms.publicize.minDelaySeconds"
        v-model:max-delay="forms.publicize.maxDelaySeconds"
        v-model:jitter="forms.publicize.jitterPercent"
      />
    </template>

    <template v-else-if="taskType === 'auto_change_login_email'">
      <el-alert
        title="默认只处理匹配 777000 登录邮箱重置通知的账号；强制执行仅用于人工确认后的补救场景。Cloud Mail URL/Token 使用系统设置页配置。"
        type="info"
        :closable="false"
        class="mb-3"
      />
      <el-form-item label="账号分类">
        <el-select v-model="forms.autoLoginEmail.categoryIds" multiple collapse-tags collapse-tags-tooltip class="full" placeholder="请选择执行账号分类">
          <el-option v-for="item in accountCategories" :key="item.id" :label="categoryLabel(item)" :value="item.id" />
        </el-select>
      </el-form-item>
      <el-form-item label="邮箱域名">
        <el-input v-model="forms.autoLoginEmail.domain" placeholder="example.com" />
        <div class="form-hint no-offset">将生成 手机号数字@域名；留空时使用系统 CloudMail:Domain。</div>
      </el-form-item>
      <el-row :gutter="12">
        <el-col :span="8">
          <el-form-item label="通知距今天数">
            <el-input-number v-model="forms.autoLoginEmail.triggerDaysAgo" :min="0" :max="30" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="通知窗口(小时)">
            <el-input-number v-model="forms.autoLoginEmail.triggerWindowHours" :min="1" :max="336" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="8">
          <el-form-item label="最多扫描通知">
            <el-input-number v-model="forms.autoLoginEmail.maxSystemMessages" :min="20" :max="1000" class="full" />
          </el-form-item>
        </el-col>
      </el-row>
      <el-form-item label="触发短语">
        <el-input v-model="forms.autoLoginEmail.triggerPhrasesText" type="textarea" :rows="3" placeholder="可留空使用默认短语；每行一个额外匹配短语" />
      </el-form-item>
      <el-row :gutter="12">
        <el-col :span="12">
          <el-form-item label="强制执行">
            <el-switch v-model="forms.autoLoginEmail.force" active-text="启用" inactive-text="关闭" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="自动确认">
            <el-switch v-model="forms.autoLoginEmail.autoConfirm" active-text="启用" inactive-text="关闭" />
          </el-form-item>
        </el-col>
      </el-row>
      <el-row v-if="forms.autoLoginEmail.autoConfirm" :gutter="12">
        <el-col :span="12">
          <el-form-item label="收码间隔(秒)">
            <el-input-number v-model="forms.autoLoginEmail.pollIntervalSeconds" :min="2" :max="30" class="full" />
          </el-form-item>
        </el-col>
        <el-col :span="12">
          <el-form-item label="收码超时(秒)">
            <el-input-number v-model="forms.autoLoginEmail.pollTimeoutSeconds" :min="10" :max="600" class="full" />
          </el-form-item>
        </el-col>
      </el-row>
    </template>

    <el-alert v-if="draft.validationError" :title="draft.validationError" type="warning" :closable="false" class="mt-2" />
  </div>
</template>

<script setup lang="ts">
import { computed, defineComponent, h, onMounted, reactive, ref, watch } from 'vue'
import { ElButton, ElCol, ElFormItem, ElInputNumber, ElOption, ElRadioButton, ElRadioGroup, ElRow, ElSelect, ElText, ElUpload, ElMessage } from 'element-plus'
import type { UploadFile } from 'element-plus'
import { panelApi } from '@/api/panel'
import type { AccountCategory, DataDictionary, OperationAccount, SimpleCategory } from '@/api/types'

type AvatarSource = 'none' | 'fixed' | 'dictionary'
type AutomationKind = 'privateCreate' | 'publicize'

export interface TaskConfigDraft {
  total: number
  config: string | null
  canSubmit: boolean
  validationError: string | null
}

interface UserChatActiveMessageRuleForm {
  id: string
  text: string
  imageDictionaryName: string
}

const props = defineProps<{
  taskType: string
  initialConfigJson?: string | null
}>()

const emit = defineEmits<{
  'draft-changed': [draft: TaskConfigDraft]
}>()

const loading = ref(false)
const accountCategories = ref<AccountCategory[]>([])
const operationAccounts = ref<OperationAccount[]>([])
const channelGroups = ref<SimpleCategory[]>([])
const groupCategories = ref<SimpleCategory[]>([])
const dictionaries = ref<DataDictionary[]>([])
const selectableAiModels = ref<string[]>([])
const globalDefaultAiModel = ref('')

const draft = reactive<TaskConfigDraft>({
  total: 0,
  config: null,
  canSubmit: false,
  validationError: '配置加载中',
})

const forms = reactive({
  userChatActive: defaultUserChatActiveForm(),
  privateCreate: defaultPrivateCreateForm(),
  publicize: defaultPublicizeForm(),
  autoLoginEmail: defaultAutoLoginEmailForm(),
})

const textDictionaryNames = computed(() =>
  dictionaries.value
    .filter((x) => x.isEnabled && x.type === 'text' && x.enabledItemCount > 0)
    .map((x) => x.name)
    .sort((a, b) => a.localeCompare(b, 'zh-Hans-CN')),
)

const imageDictionaryNames = computed(() =>
  dictionaries.value
    .filter((x) => x.isEnabled && x.type === 'image' && x.enabledItemCount > 0)
    .map((x) => x.name)
    .sort((a, b) => a.localeCompare(b, 'zh-Hans-CN')),
)

const textVariableHint = computed(() => {
  const names = ['{time}', ...textDictionaryNames.value.map((x) => `{${x}}`)]
  return names.join('、')
})

const globalAiModelLabel = computed(() => {
  const model = globalDefaultAiModel.value.trim()
  return model ? `使用全局默认（${model}）` : '使用全局默认（未配置）'
})

onMounted(loadMetadata)

watch(
  () => [props.taskType, props.initialConfigJson],
  () => {
    resetForms()
    applyInitialConfig()
    pushDraft()
  },
  { immediate: true },
)

watch(forms, pushDraft, { deep: true })

async function loadMetadata() {
  loading.value = true
  pushDraft()
  try {
    const [categories, accounts, cGroups, gCategories, dicts, settings] = await Promise.all([
      panelApi.accountCategories(),
      panelApi.operationAccounts(),
      panelApi.channelGroups(),
      panelApi.groupCategories(),
      panelApi.dictionaries(),
      panelApi.settings(),
    ])
    accountCategories.value = categories
      .filter((x) => !x.excludeFromOperations)
      .sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'))
    operationAccounts.value = accounts
    channelGroups.value = cGroups
    groupCategories.value = gCategories
    dictionaries.value = dicts
    selectableAiModels.value = Array.from(new Set(settings.ai.presetModels.map((x) => x.trim()).filter(Boolean)))
    globalDefaultAiModel.value = settings.ai.defaultModel || ''
    applyAiModelSelection(forms.userChatActive.aiModel)
    if (!forms.autoLoginEmail.domain.trim()) {
      forms.autoLoginEmail.domain = (settings.cloudMail.domain || '').replace(/^@+/, '')
    }
  } finally {
    loading.value = false
    pushDraft()
  }
}

function resetForms() {
  Object.assign(forms.userChatActive, defaultUserChatActiveForm())
  Object.assign(forms.privateCreate, defaultPrivateCreateForm())
  Object.assign(forms.publicize, defaultPublicizeForm())
  Object.assign(forms.autoLoginEmail, defaultAutoLoginEmailForm())
}

function applyInitialConfig() {
  const raw = (props.initialConfigJson || '').trim()
  if (!raw) return

  let cfg: Record<string, unknown>
  try {
    cfg = JSON.parse(raw) as Record<string, unknown>
  } catch {
    return
  }

  if (props.taskType === 'user_chat_active') {
    const form = forms.userChatActive
    form.categoryIds = normalizeIds(cfg.category_ids, readNumber(cfg.category_id))
    form.targetsText = readStringArray(cfg.targets).join('\n')
    const legacyDictionary = readStringArray(cfg.dictionary)
    const legacyImageDictionaryName = extractDictionaryName(readString(cfg.image_dictionary_token))
    form.messageRules = readUserChatActiveMessageRules(cfg.message_rules, legacyDictionary, legacyImageDictionaryName)
    form.bulkRulesText = ''
    form.delayMinSeconds = millisecondsToSeconds(readNumber(cfg.delay_min_ms, 15000))
    form.delayMaxSeconds = millisecondsToSeconds(readNumber(cfg.delay_max_ms, 45000))
    form.maxMessages = readNumber(cfg.max_messages, 0)
    form.accountMode = normalizeMode(readString(cfg.account_mode, 'random'))
    form.targetMode = normalizeMode(readString(cfg.target_mode, 'queue'))
    form.messageMode = normalizeMode(readString(cfg.message_mode, 'random'))
    form.enableAiVerification = readBoolean(cfg.enable_ai_verification)
    form.aiModel = readString(cfg.ai_model)
    form.verificationTimeoutSeconds = clamp(readNumber(cfg.verification_timeout_seconds, 15), 3, 300)
    form.verificationTimeoutAsFailure = readBoolean(cfg.verification_timeout_as_failure)
    form.verificationMatchMode = normalizeVerificationMode(readString(cfg.verification_match_mode, 'mention_or_reply'))
    form.verificationKeywordsText = readStringArray(cfg.verification_keywords).join('\n')
    form.verificationRegexText = readStringArray(cfg.verification_regexes).join('\n')
    form.verificationBotUsernameFilterEnabled = readBoolean(cfg.verification_bot_username_filter)
    form.verificationBotUsernamesText = readStringArray(cfg.verification_bot_usernames).join('\n')
    applyAiModelSelection(form.aiModel)
    return
  }

  if (props.taskType === 'channel_group_private_create') {
    const form = forms.privateCreate
    form.categoryIds = normalizeIds(cfg.category_ids)
    form.createType = normalizeObjectType(readString(cfg.create_type, 'channel'))
    form.channelGroupId = readNumber(cfg.channel_group_id, 0)
    form.groupCategoryId = readNumber(cfg.group_category_id, 0)
    form.systemCreatedLimit = Math.max(1, readNumber(cfg.system_created_limit, 10))
    form.perAccountBatchSize = Math.max(1, readNumber(cfg.per_account_batch_size, 1))
    form.minDelaySeconds = Math.max(0, readNumber(cfg.min_delay_seconds, 10))
    form.maxDelaySeconds = Math.max(0, readNumber(cfg.max_delay_seconds, 30))
    form.jitterPercent = clamp(readNumber(cfg.jitter_percent, 20), 0, 100)
    form.titleTemplate = readString(cfg.title_template)
    form.avatarSource = normalizeAvatarSource(readString(cfg.avatar_source, 'none'))
    form.fixedAvatarAssetPath = readString(cfg.fixed_avatar_asset_path)
    form.avatarDictionaryName = extractDictionaryName(readString(cfg.avatar_dictionary_token))
    form.assetScopeId = readString(cfg.asset_scope_id) || newScopeId()
    return
  }

  if (props.taskType === 'channel_group_publicize') {
    const form = forms.publicize
    form.categoryIds = normalizeIds(cfg.category_ids)
    form.targetType = normalizeObjectType(readString(cfg.target_type, 'channel'))
    form.channelGroupId = readNumber(cfg.channel_group_id, 0)
    form.groupCategoryId = readNumber(cfg.group_category_id, 0)
    form.targetChannelGroupId = readOptionalCategoryId(cfg.target_channel_group_id)
    form.targetGroupCategoryId = readOptionalCategoryId(cfg.target_group_category_id)
    form.minSystemCreatedDays = Math.max(0, readNumber(cfg.min_system_created_days, 0))
    form.maxPublicCount = Math.max(1, readNumber(cfg.max_public_count, 10))
    form.perAccountBatchSize = Math.max(1, readNumber(cfg.per_account_batch_size, 1))
    form.minDelaySeconds = Math.max(0, readNumber(cfg.min_delay_seconds, 10))
    form.maxDelaySeconds = Math.max(0, readNumber(cfg.max_delay_seconds, 30))
    form.jitterPercent = clamp(readNumber(cfg.jitter_percent, 20), 0, 100)
    form.titleTemplate = readString(cfg.title_template)
    form.descriptionTemplate = readString(cfg.description_template)
    form.usernameTemplate = readString(cfg.username_template)
    form.avatarSource = normalizeAvatarSource(readString(cfg.avatar_source, 'none'))
    form.fixedAvatarAssetPath = readString(cfg.fixed_avatar_asset_path)
    form.avatarDictionaryName = extractDictionaryName(readString(cfg.avatar_dictionary_token))
    form.assetScopeId = readString(cfg.asset_scope_id) || newScopeId()
    return
  }
  if (props.taskType === 'auto_change_login_email') {
    const form = forms.autoLoginEmail
    form.categoryIds = normalizeIds(cfg.category_ids)
    form.domain = readString(cfg.domain)
    form.triggerDaysAgo = clamp(readNumber(cfg.trigger_days_ago, 6), 0, 30)
    form.triggerWindowHours = clamp(readNumber(cfg.trigger_window_hours, 24), 1, 336)
    form.maxSystemMessages = clamp(readNumber(cfg.max_system_messages, 300), 20, 1000)
    form.force = readBoolean(cfg.force)
    form.autoConfirm = cfg.auto_confirm === undefined ? true : readBoolean(cfg.auto_confirm)
    form.pollIntervalSeconds = clamp(readNumber(cfg.poll_interval_seconds, 5), 2, 30)
    form.pollTimeoutSeconds = clamp(readNumber(cfg.poll_timeout_seconds, 90), 10, 600)
    form.triggerPhrasesText = readStringArray(cfg.trigger_phrases).join('\n')
    return
  }


}

function pushDraft() {
  let next: TaskConfigDraft
  try {
    if (loading.value) {
      next = invalidDraft('配置加载中')
    } else if (props.taskType === 'user_chat_active') {
      next = buildUserChatActiveDraft()
    } else if (props.taskType === 'channel_group_private_create') {
      next = buildPrivateCreateDraft()
    } else if (props.taskType === 'channel_group_publicize') {
      next = buildPublicizeDraft()
    } else if (props.taskType === 'auto_change_login_email') {
      next = buildAutoLoginEmailDraft()
    } else {
      next = invalidDraft('该任务类型没有专用配置表单')
    }
  } catch (error) {
    next = invalidDraft(error instanceof Error ? error.message : '任务配置无效')
  }

  Object.assign(draft, next)
  emit('draft-changed', { ...next })
}

function buildUserChatActiveDraft(): TaskConfigDraft {
  const form = forms.userChatActive
  const categoryIds = normalizedSelectedIds(form.categoryIds)
  const selectedCategories = selectedAccountCategories(categoryIds)
  const targets = uniqueLines(form.targetsText)
  const messageRules = normalizeUserChatActiveMessageRules(form.messageRules)
  const dictionary = messageRules.map((x) => x.text).filter(Boolean)
  const sharedImageDictionaryName = sharedRuleImageDictionaryName(messageRules)
  if (categoryIds.length === 0 || selectedCategories.length === 0) throw new Error('请至少选择一个执行账号分类')
  if (targets.length === 0) throw new Error('请至少填写一个目标群组/频道/Bot')
  if (messageRules.length === 0) throw new Error('请至少添加一条消息规则')
  for (const rule of messageRules) {
    if (rule.imageDictionaryName && !imageDictionaryNames.value.includes(rule.imageDictionaryName)) throw new Error('请选择有效的图片字典')
  }
  if (form.delayMaxSeconds < form.delayMinSeconds) throw new Error('最大间隔不能小于最小间隔')
  if (!isValidMode(form.accountMode) || !isValidMode(form.targetMode) || !isValidMode(form.messageMode)) throw new Error('模式参数无效')

  const verificationKeywords = form.enableAiVerification ? uniqueLines(form.verificationKeywordsText) : []
  const verificationRegexes = form.enableAiVerification ? uniqueLines(form.verificationRegexText) : []
  const verificationBotUsernames = form.enableAiVerification ? uniqueLines(form.verificationBotUsernamesText).map((x) => x.replace(/^@+/, '')) : []
  if (form.enableAiVerification) {
    if (form.selectedAiModelOption === '__custom__' && !form.customAiModel.trim()) throw new Error('请填写自定义模型名')
    if (form.verificationMatchMode === 'keyword' && verificationKeywords.length === 0) throw new Error('请至少填写一个验证关键词')
    if (form.verificationMatchMode === 'regex') {
      if (verificationRegexes.length === 0) throw new Error('请至少填写一个验证正则')
      for (const pattern of verificationRegexes) {
        try {
          new RegExp(pattern, 'i')
        } catch (error) {
          throw new Error(`验证正则无效：${error instanceof Error ? error.message : pattern}`)
        }
      }
    }
    if (form.verificationBotUsernameFilterEnabled && verificationBotUsernames.length === 0) {
      throw new Error('请至少填写一个机器人用户名或 ID')
    }
  }

  const config = {
    category_id: selectedCategories[0].id,
    category_name: selectedCategories[0].name,
    category_ids: categoryIds,
    category_names: selectedCategories.map((x) => x.name),
    targets,
    dictionary,
    image_dictionary_token: sharedImageDictionaryName ? dictionaryToken(sharedImageDictionaryName) : null,
    message_rules: messageRules.map((rule) => ({
      text: rule.text,
      image_dictionary_token: rule.imageDictionaryName ? dictionaryToken(rule.imageDictionaryName) : null,
    })),
    delay_min_ms: secondsToMilliseconds(form.delayMinSeconds),
    delay_max_ms: secondsToMilliseconds(form.delayMaxSeconds),
    account_mode: form.accountMode,
    message_mode: form.messageMode,
    target_mode: form.targetMode,
    max_messages: Math.max(0, form.maxMessages),
    enable_ai_verification: form.enableAiVerification,
    ai_model: form.enableAiVerification ? resolveAiModel() : null,
    verification_timeout_seconds: form.enableAiVerification ? form.verificationTimeoutSeconds : 15,
    verification_timeout_as_failure: form.enableAiVerification && form.verificationTimeoutAsFailure,
    verification_match_mode: normalizeVerificationMode(form.verificationMatchMode),
    verification_keywords: verificationKeywords,
    verification_regexes: verificationRegexes,
    verification_bot_username_filter: form.enableAiVerification && form.verificationBotUsernameFilterEnabled,
    verification_bot_usernames: verificationBotUsernames,
  }

  return validDraft(Math.max(0, form.maxMessages), config)
}

function buildPrivateCreateDraft(): TaskConfigDraft {
  const form = forms.privateCreate
  const categoryIds = normalizedSelectedIds(form.categoryIds)
  const selectedCategories = selectedAccountCategories(categoryIds)
  if (categoryIds.length === 0 || selectedCategories.length === 0) throw new Error('请至少选择一个执行账号分类')
  if (!form.titleTemplate.trim()) throw new Error('标题模板不能为空')
  validateDelay(form.minDelaySeconds, form.maxDelaySeconds)
  validateAvatar(form.avatarSource, form.fixedAvatarAssetPath, form.avatarDictionaryName)

  const isChannel = form.createType === 'channel'
  const config = {
    category_ids: categoryIds,
    category_names: selectedCategories.map((x) => x.name),
    create_type: isChannel ? 'channel' : 'group',
    channel_group_id: isChannel && form.channelGroupId > 0 ? form.channelGroupId : null,
    channel_group_name: isChannel && form.channelGroupId > 0 ? channelGroups.value.find((x) => x.id === form.channelGroupId)?.name || null : null,
    group_category_id: !isChannel && form.groupCategoryId > 0 ? form.groupCategoryId : null,
    group_category_name: !isChannel && form.groupCategoryId > 0 ? groupCategories.value.find((x) => x.id === form.groupCategoryId)?.name || null : null,
    system_created_limit: Math.max(1, form.systemCreatedLimit),
    per_account_batch_size: Math.max(1, form.perAccountBatchSize),
    min_delay_seconds: Math.max(0, form.minDelaySeconds),
    max_delay_seconds: Math.max(0, form.maxDelaySeconds),
    jitter_percent: clamp(form.jitterPercent, 0, 100),
    title_template: form.titleTemplate.trim(),
    avatar_source: form.avatarSource,
    fixed_avatar_asset_path: form.avatarSource === 'fixed' ? form.fixedAvatarAssetPath.trim() : null,
    avatar_dictionary_token: form.avatarSource === 'dictionary' ? dictionaryToken(form.avatarDictionaryName) : null,
    asset_scope_id: form.avatarSource === 'fixed' ? form.assetScopeId : null,
  }

  return validDraft(automationTotal(categoryIds, form.perAccountBatchSize), config)
}

function buildPublicizeDraft(): TaskConfigDraft {
  const form = forms.publicize
  const categoryIds = normalizedSelectedIds(form.categoryIds)
  const selectedCategories = selectedAccountCategories(categoryIds)
  if (categoryIds.length === 0 || selectedCategories.length === 0) throw new Error('请至少选择一个执行账号分类')
  if (!form.titleTemplate.trim()) throw new Error('标题模板不能为空')
  if (!form.usernameTemplate.trim()) throw new Error('公开用户名模板不能为空')
  validateDelay(form.minDelaySeconds, form.maxDelaySeconds)
  validateAvatar(form.avatarSource, form.fixedAvatarAssetPath, form.avatarDictionaryName)

  const isChannel = form.targetType === 'channel'
  const targetChannelGroupId = normalizeOptionalCategoryId(form.targetChannelGroupId)
  const targetGroupCategoryId = normalizeOptionalCategoryId(form.targetGroupCategoryId)
  const config = {
    category_ids: categoryIds,
    category_names: selectedCategories.map((x) => x.name),
    target_type: isChannel ? 'channel' : 'group',
    channel_group_id: isChannel && form.channelGroupId > 0 ? form.channelGroupId : null,
    channel_group_name: isChannel && form.channelGroupId > 0 ? channelGroups.value.find((x) => x.id === form.channelGroupId)?.name || null : null,
    group_category_id: !isChannel && form.groupCategoryId > 0 ? form.groupCategoryId : null,
    group_category_name: !isChannel && form.groupCategoryId > 0 ? groupCategories.value.find((x) => x.id === form.groupCategoryId)?.name || null : null,
    target_channel_group_id: isChannel ? targetChannelGroupId : null,
    target_channel_group_name: isChannel ? optionalChannelGroupName(targetChannelGroupId) : null,
    target_group_category_id: !isChannel ? targetGroupCategoryId : null,
    target_group_category_name: !isChannel ? optionalGroupCategoryName(targetGroupCategoryId) : null,
    min_system_created_days: Math.max(0, form.minSystemCreatedDays),
    max_public_count: Math.max(1, form.maxPublicCount),
    per_account_batch_size: Math.max(1, form.perAccountBatchSize),
    min_delay_seconds: Math.max(0, form.minDelaySeconds),
    max_delay_seconds: Math.max(0, form.maxDelaySeconds),
    jitter_percent: clamp(form.jitterPercent, 0, 100),
    title_template: form.titleTemplate.trim(),
    description_template: form.descriptionTemplate.trim(),
    username_template: form.usernameTemplate.trim(),
    avatar_source: form.avatarSource,
    fixed_avatar_asset_path: form.avatarSource === 'fixed' ? form.fixedAvatarAssetPath.trim() : null,
    avatar_dictionary_token: form.avatarSource === 'dictionary' ? dictionaryToken(form.avatarDictionaryName) : null,
    asset_scope_id: form.avatarSource === 'fixed' ? form.assetScopeId : null,
  }

  return validDraft(automationTotal(categoryIds, form.perAccountBatchSize), config)
}

function buildAutoLoginEmailDraft(): TaskConfigDraft {
  const form = forms.autoLoginEmail
  const categoryIds = normalizedSelectedIds(form.categoryIds)
  const selectedCategories = selectedAccountCategories(categoryIds)
  if (categoryIds.length === 0 || selectedCategories.length === 0) throw new Error('请至少选择一个执行账号分类')
  const triggerPhrases = uniqueLines(form.triggerPhrasesText)
  const config = {
    category_ids: categoryIds,
    category_names: selectedCategories.map((x) => x.name),
    domain: form.domain.trim().replace(/^@+/, '') || null,
    trigger_days_ago: clamp(form.triggerDaysAgo, 0, 30),
    trigger_window_hours: clamp(form.triggerWindowHours, 1, 336),
    max_system_messages: clamp(form.maxSystemMessages, 20, 1000),
    force: form.force,
    auto_confirm: form.autoConfirm,
    poll_interval_seconds: clamp(form.pollIntervalSeconds, 2, 30),
    poll_timeout_seconds: clamp(form.pollTimeoutSeconds, 10, 600),
    trigger_phrases: triggerPhrases,
  }

  return validDraft(automationTotal(categoryIds, 1), config)
}


async function uploadAvatar(kind: AutomationKind, file: UploadFile) {
  const raw = file.raw
  if (!raw) return

  const formState = forms[kind]
  formState.uploadingAvatar = true
  try {
    const form = new FormData()
    form.append('scopeId', formState.assetScopeId)
    form.append('file', raw)
    const result = await panelApi.uploadTaskAvatarAsset(form)
    formState.assetScopeId = result.scopeId
    formState.fixedAvatarAssetPath = result.assetPath
    formState.avatarSource = 'fixed'
    ElMessage.success('固定头像已上传')
  } finally {
    formState.uploadingAvatar = false
  }
}

function applyAiModelSelection(model?: string | null) {
  const normalized = (model || '').trim()
  const form = forms.userChatActive
  form.aiModel = normalized
  if (!normalized) {
    form.selectedAiModelOption = '__global__'
    form.customAiModel = ''
    return
  }

  const preset = selectableAiModels.value.find((x) => x.toLowerCase() === normalized.toLowerCase())
  if (preset) {
    form.selectedAiModelOption = preset
    form.customAiModel = ''
    return
  }

  form.selectedAiModelOption = '__custom__'
  form.customAiModel = normalized
}

function resolveAiModel() {
  const form = forms.userChatActive
  if (form.selectedAiModelOption === '__global__') return null
  if (form.selectedAiModelOption === '__custom__') return form.customAiModel.trim() || null
  return form.selectedAiModelOption.trim() || null
}

function selectedAccountCategories(ids: number[]) {
  const set = new Set(ids)
  return accountCategories.value.filter((x) => set.has(x.id))
}

function automationTotal(categoryIds: number[], perAccountBatchSize: number) {
  const set = new Set(categoryIds)
  const accountCount = operationAccounts.value.filter((x) => x.isActive && x.categoryId && set.has(x.categoryId)).length
  return Math.max(0, accountCount * Math.max(1, perAccountBatchSize))
}

function validateDelay(min: number, max: number) {
  if (min < 0 || max < 0) throw new Error('间隔不能为负数')
  if (max < min) throw new Error('最大间隔不能小于最小间隔')
}

function validateAvatar(source: AvatarSource, fixedPath: string, dictionaryName: string) {
  if (source === 'fixed' && !fixedPath.trim()) throw new Error('请先上传固定头像')
  if (source === 'dictionary' && !dictionaryName.trim()) throw new Error('请选择图片字典')
}

function categoryLabel(item: AccountCategory) {
  return `${item.name} (${item.accountCount})`
}

function validDraft(total: number, config: unknown): TaskConfigDraft {
  return {
    total: Math.max(0, total),
    config: JSON.stringify(config, null, 2),
    canSubmit: true,
    validationError: null,
  }
}

function invalidDraft(message: string): TaskConfigDraft {
  return { total: 0, config: null, canSubmit: false, validationError: message }
}

function defaultUserChatActiveForm() {
  return {
    categoryIds: [] as number[],
    targetsText: '',
    messageRules: [defaultUserChatActiveMessageRule()],
    bulkRulesText: '',
    delayMinSeconds: 15,
    delayMaxSeconds: 45,
    maxMessages: 0,
    accountMode: 'random',
    targetMode: 'queue',
    messageMode: 'random',
    enableAiVerification: false,
    aiModel: '',
    selectedAiModelOption: '__global__',
    customAiModel: '',
    verificationTimeoutSeconds: 15,
    verificationTimeoutAsFailure: false,
    verificationMatchMode: 'mention_or_reply',
    verificationKeywordsText: '',
    verificationRegexText: '',
    verificationBotUsernameFilterEnabled: false,
    verificationBotUsernamesText: '',
  }
}

function defaultUserChatActiveMessageRule(text = '', imageDictionaryName = ''): UserChatActiveMessageRuleForm {
  return {
    id: newScopeId(),
    text,
    imageDictionaryName,
  }
}

function addUserChatActiveRule(text = '', imageDictionaryName = '') {
  forms.userChatActive.messageRules.push(defaultUserChatActiveMessageRule(text, imageDictionaryName))
}

function removeUserChatActiveRule(index: number) {
  if (forms.userChatActive.messageRules.length <= 1) return
  forms.userChatActive.messageRules.splice(index, 1)
}

function appendUserChatActiveLineRules() {
  const lines = parseLines(forms.userChatActive.bulkRulesText)
  if (lines.length === 0) {
    ElMessage.warning('请先填写要追加的消息文字')
    return
  }

  const rules = forms.userChatActive.messageRules
  if (rules.length === 1 && !rules[0].text.trim() && !rules[0].imageDictionaryName.trim()) {
    rules.splice(0, 1)
  }

  for (const line of lines) {
    rules.push(defaultUserChatActiveMessageRule(line))
  }
  forms.userChatActive.bulkRulesText = ''
}


function defaultPrivateCreateForm() {
  return {
    categoryIds: [] as number[],
    createType: 'channel',
    channelGroupId: 0,
    groupCategoryId: 0,
    titleTemplate: '',
    systemCreatedLimit: 10,
    perAccountBatchSize: 1,
    minDelaySeconds: 10,
    maxDelaySeconds: 30,
    jitterPercent: 20,
    avatarSource: 'none' as AvatarSource,
    fixedAvatarAssetPath: '',
    avatarDictionaryName: '',
    assetScopeId: newScopeId(),
    uploadingAvatar: false,
  }
}

function defaultPublicizeForm() {
  return {
    categoryIds: [] as number[],
    targetType: 'channel',
    channelGroupId: 0,
    groupCategoryId: 0,
    targetChannelGroupId: -1,
    targetGroupCategoryId: -1,
    titleTemplate: '',
    descriptionTemplate: '',
    usernameTemplate: '',
    minSystemCreatedDays: 0,
    maxPublicCount: 10,
    perAccountBatchSize: 1,
    minDelaySeconds: 10,
    maxDelaySeconds: 30,
    jitterPercent: 20,
    avatarSource: 'none' as AvatarSource,
    fixedAvatarAssetPath: '',
    avatarDictionaryName: '',
    assetScopeId: newScopeId(),
    uploadingAvatar: false,
  }
}

function defaultAutoLoginEmailForm() {
  return {
    categoryIds: [] as number[],
    domain: '',
    triggerDaysAgo: 6,
    triggerWindowHours: 24,
    maxSystemMessages: 300,
    force: false,
    autoConfirm: true,
    pollIntervalSeconds: 5,
    pollTimeoutSeconds: 90,
    triggerPhrasesText: '',
  }
}

function readUserChatActiveMessageRules(value: unknown, legacyDictionary: string[], legacyImageDictionaryName: string) {
  const rules: UserChatActiveMessageRuleForm[] = []
  if (Array.isArray(value)) {
    for (const item of value) {
      if (!item || typeof item !== 'object') continue
      const record = item as Record<string, unknown>
      const text = normalizeMultilineText(readString(record.text))
      const imageDictionaryName = extractDictionaryName(readString(record.image_dictionary_token))
      if (text || imageDictionaryName) rules.push(defaultUserChatActiveMessageRule(text, imageDictionaryName))
    }
  }

  if (rules.length > 0) return rules
  if (legacyDictionary.length > 0) return legacyDictionary.map((text) => defaultUserChatActiveMessageRule(text, legacyImageDictionaryName))
  if (legacyImageDictionaryName) return [defaultUserChatActiveMessageRule('', legacyImageDictionaryName)]
  return [defaultUserChatActiveMessageRule()]
}

function normalizeUserChatActiveMessageRules(rules: UserChatActiveMessageRuleForm[]) {
  return rules
    .map((rule) => ({
      text: normalizeMultilineText(rule.text),
      imageDictionaryName: rule.imageDictionaryName.trim(),
    }))
    .filter((rule) => rule.text || rule.imageDictionaryName)
}

function sharedRuleImageDictionaryName(rules: Array<{ imageDictionaryName: string }>) {
  const names = Array.from(new Set(rules.map((x) => x.imageDictionaryName.trim())))
  return names.length === 1 && names[0] ? names[0] : ''
}

function normalizeMultilineText(value: string) {
  return value.replace(/\r\n/g, '\n').replace(/\r/g, '\n').trim()
}



function parseLines(value: string) {
  return value.split(/\r?\n/).map((x) => x.trim()).filter(Boolean)
}

function uniqueLines(value: string) {
  return Array.from(new Set(parseLines(value)))
}

function normalizedSelectedIds(values: number[]) {
  return Array.from(new Set(values.map((x) => Number(x)).filter((x) => Number.isFinite(x) && x > 0)))
}

function readOptionalCategoryId(value: unknown) {
  if (value === null || value === undefined) return -1
  const n = Number(value)
  return Number.isFinite(n) ? Math.max(0, Math.trunc(n)) : -1
}

function normalizeOptionalCategoryId(value: number) {
  const n = Number(value)
  if (!Number.isFinite(n) || n < 0) return null
  return Math.trunc(n)
}

function optionalChannelGroupName(id: number | null) {
  if (id === null) return '保持原分组'
  if (id <= 0) return '未分组'
  return channelGroups.value.find((x) => x.id === id)?.name || null
}

function optionalGroupCategoryName(id: number | null) {
  if (id === null) return '保持原分类'
  if (id <= 0) return '未分类'
  return groupCategories.value.find((x) => x.id === id)?.name || null
}

function normalizeIds(value: unknown, fallback = 0) {
  const ids = Array.isArray(value)
    ? value.map((x) => Number(x)).filter((x) => Number.isFinite(x) && x > 0)
    : []
  if (ids.length === 0 && fallback > 0) ids.push(fallback)
  return Array.from(new Set(ids))
}

function readStringArray(value: unknown) {
  return Array.isArray(value) ? value.map((x) => String(x ?? '').trim()).filter(Boolean) : []
}

function readString(value: unknown, fallback = '') {
  return typeof value === 'string' ? value.trim() : fallback
}

function readNumber(value: unknown, fallback = 0) {
  const n = Number(value)
  return Number.isFinite(n) ? n : fallback
}

function readBoolean(value: unknown) {
  return value === true
}

function secondsToMilliseconds(value: number) {
  return Math.round(Math.max(0, value) * 1000)
}

function millisecondsToSeconds(value: number) {
  return Math.round(Math.max(0, value) / 10) / 100
}

function normalizeMode(value: string) {
  return value.toLowerCase() === 'queue' ? 'queue' : 'random'
}

function isValidMode(value: string) {
  return value === 'random' || value === 'queue'
}

function normalizeVerificationMode(value: string) {
  if (value === 'keyword' || value === 'regex') return value
  return 'mention_or_reply'
}

function normalizeObjectType(value: string) {
  return value.toLowerCase() === 'group' ? 'group' : 'channel'
}

function normalizeAvatarSource(value: string): AvatarSource {
  if (value === 'fixed' || value === 'dictionary') return value
  return 'none'
}

function dictionaryToken(name: string) {
  return name.trim() ? `{${name.trim().replace(/[{}]/g, '')}}` : null
}

function extractDictionaryName(token: string) {
  const text = token.trim()
  return text.startsWith('{') && text.endsWith('}') ? text.slice(1, -1) : ''
}

function clamp(value: number, min: number, max: number) {
  return Math.min(max, Math.max(min, value))
}

function newScopeId() {
  if (globalThis.crypto?.randomUUID) return globalThis.crypto.randomUUID().replace(/-/g, '')
  return Math.random().toString(16).slice(2) + Date.now().toString(16)
}

const DelayFields = defineComponent({
  name: 'DelayFields',
  props: {
    minDelay: { type: Number, required: true },
    maxDelay: { type: Number, required: true },
    jitter: { type: Number, required: true },
  },
  emits: ['update:minDelay', 'update:maxDelay', 'update:jitter'],
  setup(props, { emit }) {
    return () => h(ElRow, { gutter: 12 }, () => [
      h(ElCol, { span: 8 }, () => h(ElFormItem, { label: '最小间隔' }, () =>
        h(ElInputNumber, { modelValue: props.minDelay, min: 0, max: 3600, class: 'full', 'onUpdate:modelValue': (v: number | undefined) => emit('update:minDelay', v ?? 0) }),
      )),
      h(ElCol, { span: 8 }, () => h(ElFormItem, { label: '最大间隔' }, () =>
        h(ElInputNumber, { modelValue: props.maxDelay, min: 0, max: 3600, class: 'full', 'onUpdate:modelValue': (v: number | undefined) => emit('update:maxDelay', v ?? 0) }),
      )),
      h(ElCol, { span: 8 }, () => h(ElFormItem, { label: '抖动%' }, () =>
        h(ElInputNumber, { modelValue: props.jitter, min: 0, max: 100, class: 'full', 'onUpdate:modelValue': (v: number | undefined) => emit('update:jitter', v ?? 0) }),
      )),
    ])
  },
})

const AvatarFields = defineComponent({
  name: 'AvatarFields',
  props: {

    avatarSource: { type: String, required: true },
    fixedAvatarAssetPath: { type: String, required: true },
    avatarDictionaryName: { type: String, required: true },

    imageDictionaries: { type: Array<string>, required: true },
    uploading: { type: Boolean, required: true },
  },
  emits: ['update:avatarSource', 'update:avatarDictionaryName', 'upload'],
  setup(props, { emit }) {
    return () => [
      h(ElFormItem, { label: '头像来源' }, () =>
        h(ElRadioGroup, {
          modelValue: props.avatarSource,
          'onUpdate:modelValue': (v: string | number | boolean | undefined) => emit('update:avatarSource', String(v || 'none')),
        }, () => [
          h(ElRadioButton, { label: 'none' }, () => '不设置'),
          h(ElRadioButton, { label: 'fixed' }, () => '固定上传'),
          h(ElRadioButton, { label: 'dictionary' }, () => '图片字典变量'),
        ]),
      ),
      props.avatarSource === 'fixed'
        ? h(ElFormItem, { label: '固定头像' }, () => [
          h(ElUpload, {
            autoUpload: false,
            limit: 1,
            accept: 'image/*',
            showFileList: false,
            onChange: (file: UploadFile) => emit('upload', file),
          }, () => h(ElButton, { loading: props.uploading }, () => props.fixedAvatarAssetPath ? '重新上传头像' : '上传固定头像')),
          props.fixedAvatarAssetPath
            ? h(ElText, { class: 'avatar-path', type: 'info', size: 'small' }, () => `已保存头像：${props.fixedAvatarAssetPath}`)
            : null,
        ])
        : null,
      props.avatarSource === 'dictionary'
        ? h(ElFormItem, { label: '图片字典' }, () =>
          h(ElSelect, {
            modelValue: props.avatarDictionaryName,
            class: 'full',
            placeholder: '请选择图片字典',
            'onUpdate:modelValue': (v: string) => emit('update:avatarDictionaryName', v),
          }, () => [
            h(ElOption, { label: '-- 请选择图片字典 --', value: '' }),
            ...props.imageDictionaries.map((name) => h(ElOption, { key: name, label: name, value: name })),
          ]),
        )
        : null,
    ]
  },
})
</script>

<style scoped>
.full {
  width: 100%;
}

.form-hint {
  margin: -8px 0 14px 96px;
  color: var(--el-text-color-secondary);
  font-size: 12px;
  line-height: 1.5;
}

.form-hint.no-offset {
  margin-left: 0;
}
.message-rule-section {
  margin-bottom: 14px;
}

.message-rule-toolbar {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 10px;
}

.message-rule-card {
  padding: 12px 12px 2px;
  margin-bottom: 10px;
  border: 1px solid var(--el-border-color-light);
  border-radius: 8px;
  background: var(--el-fill-color-lighter);
}

.message-rule-card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 8px;
  color: var(--el-text-color-regular);
  font-weight: 600;
}

.form-hint.compact {
  margin-bottom: 0;
}

.avatar-path {
  display: block;
  margin-top: 8px;
  word-break: break-all;
}
</style>
