import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const tasksSource = await readFile(new URL('../src/views/Tasks.vue', import.meta.url), 'utf8')
const taskConfigFormSource = await readFile(new URL('../src/components/TaskConfigForm.vue', import.meta.url), 'utf8')
const accountsSource = await readFile(new URL('../src/views/Accounts.vue', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

test('新建任务展示宿主前端支持的专用表单任务类型', () => {
  assert.match(typesSource, /canCreate:\s*boolean/)
  assert.match(
    tasksSource,
    /definitions\.value\.filter\(\(x\) => hasTaskConfigForm\(x\.taskType\) && \(x\.canCreate \|\| x\.category !== 'system'\)\)/,
  )
  assert.match(tasksSource, /taskCenterCreateDefinitions\.value\.map/)
  assert.match(tasksSource, /taskCenterCreateDefinitions\.value\s*\.filter/)
})

test('Fragment 用户名监控在任务中心使用独立配置表单', () => {
  assert.match(tasksSource, /taskType === 'fragment_username_monitor'/)
  assert.match(tasksSource, /if \(!definition \|\| hasTaskConfigForm\(definition\.taskType\)\) return ''/)
  assert.match(tasksSource, /editRoute && !hasTaskConfigForm\(def\.taskType\)/)
  assert.match(taskConfigFormSource, /taskType === 'fragment_username_monitor'/)
  assert.match(taskConfigFormSource, /Usernames:/)
  assert.match(taskConfigFormSource, /TargetGroupIds:/)
  assert.match(taskConfigFormSource, /CheckIntervalSeconds:/)
  assert.match(taskConfigFormSource, /AssignedUsernames: \[\]/)
})

test('独立模块任务编辑时携带任务 ID 返回模块页面', () => {
  assert.match(tasksSource, /resolveCreateTarget\(def\)/)
  assert.match(tasksSource, /taskId=\$\{encodeURIComponent\(String\(task\.id\)\)\}/)
  assert.match(tasksSource, /withModulePageMode\(routeWithTaskId, false\)/)
})

test('任务复制对模块任务走通用 JSON 新建表单', () => {
  assert.match(tasksSource, /CopyDocument/)
  assert.match(tasksSource, /v-if="canCopyTask\(row\)"[\s\S]*?title="复制"[\s\S]*?@click="copyTask\(row\)"/)
  assert.match(tasksSource, /v-if="canCopyScheduled\(row\)"[\s\S]*?title="复制"[\s\S]*?@click="copyScheduledTask\(row\)"/)
  assert.match(tasksSource, /function canCopyDefinition\(taskType: string\) \{\s*return !!taskType\.trim\(\) && !!taskDefinition\(taskType\)\s*\}/)
  assert.match(tasksSource, /if \(createDialog\.value\.sourceTaskId > 0\) return ''/)
  assert.match(tasksSource, /sourceDescription: `执行任务 #\$\{fullTask\.id\}`/)
  assert.match(tasksSource, /sourceDescription: `计划任务 #\$\{fullTask\.id\}`/)
  assert.match(tasksSource, /:model-value="createDialog\.sourceTaskId > 0 \? \['json'\] : \[\]"/)
  assert.match(tasksSource, /:initial-config-json="createDialog\.form\.config"/)
})

test('宿主任务页面和通用表单不植入独立举报模块', () => {
  assert.doesNotMatch(tasksSource, /user_message_report/)
  assert.doesNotMatch(taskConfigFormSource, /user_message_report/)

  for (const source of [tasksSource, taskConfigFormSource]) {
    assert.doesNotMatch(source, /messageReport|BuildMessageReport|reportPresetName/)
  }
})

test('账号编号输入说明和解析支持逗号与顿号', () => {
  assert.match(taskConfigFormSource, /accountNumbersPlaceholder = '可选：每行一个，或用英文逗号、中文逗号、顿号分隔；如 #1,#2、#3'/)
  assert.match(taskConfigFormSource, /:placeholder="accountNumbersPlaceholder"/)
  assert.match(taskConfigFormSource, /\.split\(\/\[\\s,，、;；\]\+\//)
})

test('自动更改登录邮箱支持多域名池和原域名避让说明', () => {
  assert.match(taskConfigFormSource, /label="邮箱域名池"/)
  assert.match(taskConfigFormSource, /domains,/)
  assert.match(taskConfigFormSource, /domain: domains\[0\] \|\| null/)
  assert.match(taskConfigFormSource, /function normalizeEmailDomains\(value: unknown\)/)
  assert.match(taskConfigFormSource, /多个域名会优先避开账号当前登录邮箱掩码中的原域名/)
  assert.match(tasksSource, /邮箱域名池: \$\{domainText\}/)
  assert.match(tasksSource, /previous_login_email_domain/)
})

test('账号详情展示登录邮箱状态', () => {
  assert.match(accountsSource, /<el-descriptions-item label="登录邮箱">/)
  assert.match(accountsSource, /panelApi\.loginEmailStatus\(row\.id\)/)
  assert.match(accountsSource, /function formatLoginEmailStatus\(status: LoginEmailStatus \| null\)/)
})

test('待执行或执行中任务编辑前必须经过暂停屏障', () => {
  assert.match(
    tasksSource,
    /return !\['pending', 'running'\]\.includes\(status\) \|\| def\.autoPauseBeforeEdit/,
  )

  const editStart = tasksSource.indexOf("  if (status === 'pending' || status === 'running') {")
  const editEnd = tasksSource.indexOf('  const editRoute = resolveCreateTarget(def)', editStart)
  assert.ok(editStart >= 0 && editEnd > editStart, '找不到 pending/running 编辑前的暂停屏障代码块')
  const editBlock = tasksSource.slice(editStart, editEnd)
  assert.match(editBlock, /if \(!def\.autoPauseBeforeEdit\)/)
  assert.match(editBlock, /await ElMessageBox\.confirm\(/)
  assert.match(editBlock, /await panelApi\.pauseTask\(task\.id\)/)
  assert.match(editBlock, /await load\(\)/)

  const confirmIndex = editBlock.indexOf('await ElMessageBox.confirm')
  const pauseIndex = editBlock.indexOf('await panelApi.pauseTask(task.id)')
  const reloadIndex = editBlock.indexOf('await load()')
  assert.ok(confirmIndex < pauseIndex && pauseIndex < reloadIndex, '暂停屏障调用顺序必须为确认、暂停、刷新')
})

test('账号持续活跃支持回复消息与转发来源配置', () => {
  assert.match(taskConfigFormSource, /messageActionMode:\s*'send_generated_text'/)
  assert.match(taskConfigFormSource, /reply_to_message_url:/)
  assert.doesNotMatch(taskConfigFormSource, /reply_to_message_id|replyToMessageId|回复消息 ID/)
  assert.match(taskConfigFormSource, /forward_source_urls:/)
  assert.match(taskConfigFormSource, /forward_mode:/)
  assert.match(taskConfigFormSource, /skip_if_last_message_from_self:/)
  assert.match(taskConfigFormSource, /skipIfLastMessageFromSelf:\s*false/)
  assert.match(taskConfigFormSource, /去重发送/)
  assert.match(taskConfigFormSource, /转发来源消息链接/)
  assert.match(taskConfigFormSource, /<el-form-item label="转发来源消息链接" label-width="128px">/)
  assert.match(tasksSource, /发送动作: \$\{isForwardMode \? '转发消息链接' : '发送消息规则'\}/)
  assert.match(tasksSource, /去重发送: \$\{skipIfLastMessageFromSelf \? '启用/)
  assert.match(taskConfigFormSource, /v-if="forms\.userChatActive\.messageActionMode === 'send_generated_text'" :span="8"/)
  assert.match(taskConfigFormSource, /message_mode: effectiveMessageMode/)
  assert.match(tasksSource, /\.\.\.\(isForwardMode \? \[\] : \[`内容模式:/)
  assert.match(taskConfigFormSource, /也可单独填写一个文本字典变量，例如 \{forward_sources\}/)
  assert.match(taskConfigFormSource, /validateUserChatActiveForwardSourceDictionaries\(forwardSourceUrls\)/)
  assert.match(taskConfigFormSource, /account_queue_cursor: Math\.max\(0, form\.accountQueueCursor\)/)

})

test('账号持续活跃目标字段说明并校验文本字典变量', () => {
  assert.match(taskConfigFormSource, /目标支持固定群组\/频道\/Bot 用户名\/链接，也支持单个文本字典变量/)
  assert.match(taskConfigFormSource, /placeholder="每行一个固定目标，或单独填写一个文本字典变量，例如 \{groups\}"/)
  assert.match(taskConfigFormSource, /支持文本字典变量：\{\{ targetVariableHint \}\}/)
  assert.match(taskConfigFormSource, /function validateUserChatActiveTargetDictionaries\(targets: string\[\]\)/)
  assert.match(taskConfigFormSource, /目标字典不能使用内置时间变量 \{time\}/)
  assert.match(taskConfigFormSource, /不是已启用且有内容的文本字典/)
  assert.match(taskConfigFormSource, /validateUserChatActiveTargetDictionaries\(targets\)/)
})
test('任务弹窗在手机端收窄并纵向排版', () => {
  assert.match(tasksSource, /width="min\(760px, calc\(100vw - 24px\)\)"/)
  assert.match(tasksSource, /width="min\(720px, calc\(100vw - 24px\)\)"/)
  assert.match(tasksSource, /:label-position="isTaskDialogCompact \? 'top' : 'right'"/)
  assert.match(tasksSource, /:label-width="isTaskDialogCompact \? 'auto' : '96px'"/)
  assert.match(tasksSource, /v-model="editScheduledDialog\.visible"[\s\S]*?width="min\(760px, calc\(100vw - 24px\)\)"/)
  assert.match(tasksSource, /v-model="editScheduledDialog\.visible"[\s\S]*?:label-position="isTaskDialogCompact \? 'top' : 'right'"/)
  assert.match(tasksSource, /class="task-dialog"/)
  assert.match(tasksSource, /:global\(\.task-dialog\)/)
  assert.match(tasksSource, /max-height: calc\(100vh - 24px\);/)
  assert.match(tasksSource, /overflow-y: auto;/)
  assert.match(tasksSource, /:global\(\.task-dialog \.el-dialog__footer\)/)
  assert.match(tasksSource, /:xs="24" :sm="12"/)
  assert.match(taskConfigFormSource, /@media \(max-width: 640px\)/)
  assert.match(taskConfigFormSource, /grid-template-columns: 1fr;/)
  assert.match(taskConfigFormSource, /:deep\(\.el-form-item__content\)/)
})

test('新建和编辑即时任务支持自定义任务名称', () => {
  assert.match(typesSource, /name\?:\s*string \| null/)
  assert.match(tasksSource, /<el-form-item v-if="!currentCreateTarget" label="任务名称">/)
  assert.match(tasksSource, /placeholder="可选，留空则显示任务类型和 ID"/)
  assert.match(tasksSource, /@change="onCreateModeChanged"/)
  assert.match(tasksSource, /function onCreateModeChanged\(mode: string \| number \| boolean \| undefined\)/)
  assert.match(tasksSource, /const taskDisplayName = form\.name\.trim\(\)/)
  assert.match(tasksSource, /name:\s*taskDisplayName \|\| null/)
  assert.match(tasksSource, /name:\s*fullTask\.name\?\.trim\(\) \|\| ''/)
  assert.match(tasksSource, /name:\s*dialog\.form\.name\.trim\(\)/)
  assert.match(tasksSource, /name:\s*fullTask\.name\?\.trim\(\) \|\| null/)
})

test('任务账号来源只能在分类和编号之间二选一', () => {
  assert.match(taskConfigFormSource, /账号来源/)
  assert.match(taskConfigFormSource, /账号分类选择/)
  assert.match(taskConfigFormSource, /账号编号填写/)
  assert.match(taskConfigFormSource, /accountSourceMode === 'category'/)
  assert.match(taskConfigFormSource, /activeAccountSource\(form\)/)
  assert.doesNotMatch(taskConfigFormSource, /与账号分类合并执行/)
  assert.doesNotMatch(taskConfigFormSource, /请至少选择账号分类或填写账号编号/)
})

test('任务操作支持复制到新建表单而不是立即重跑', () => {
  assert.match(tasksSource, /CopyDocument/)
  assert.match(tasksSource, /title="复制"/)
  assert.match(tasksSource, /@click="copyTask\(row\)"/)
  assert.match(tasksSource, /@click="copyScheduledTask\(row\)"/)
  assert.match(tasksSource, /initial-config-json="createDialog\.form\.config"/)
  assert.match(tasksSource, /已复制任务 #\$\{createDialog\.sourceTaskId\} 的配置/)
  assert.match(tasksSource, /function copyTask\(task: BatchTask\)/)
  assert.match(tasksSource, /function copyScheduledTask\(task: ScheduledTask\)/)
  assert.match(tasksSource, /function openCopiedCreateDialog/)
  assert.match(tasksSource, /stripRuntimeFields\(fullTask\.taskType, fullTask\.config\)/)
})
