import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const tasksSource = await readFile(new URL('../src/views/Tasks.vue', import.meta.url), 'utf8')
const taskConfigFormSource = await readFile(new URL('../src/components/TaskConfigForm.vue', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

test('新建任务只展示宿主明确支持且有专用表单的任务类型', () => {
  assert.match(typesSource, /canCreate:\s*boolean/)
  assert.match(
    tasksSource,
    /definitions\.value\.filter\(\(x\) => x\.canCreate && hasTaskConfigForm\(x\.taskType\) && x\.category !== 'system'\)/,
  )
  assert.match(tasksSource, /taskCenterCreateDefinitions\.value\.map/)
  assert.match(tasksSource, /taskCenterCreateDefinitions\.value\s*\.filter/)
})

test('独立模块任务编辑时携带任务 ID 返回模块页面', () => {
  assert.match(tasksSource, /resolveCreateTarget\(def\)/)
  assert.match(tasksSource, /taskId=\$\{encodeURIComponent\(String\(task\.id\)\)\}/)
  assert.match(tasksSource, /withModulePageMode\(routeWithTaskId, false\)/)
})

test('宿主任务页面和通用表单不植入独立举报模块', () => {
  assert.doesNotMatch(tasksSource, /user_message_report/)
  assert.doesNotMatch(taskConfigFormSource, /user_message_report/)

  for (const source of [tasksSource, taskConfigFormSource]) {
    assert.doesNotMatch(source, /messageReport|BuildMessageReport|reportPresetName/)
  }
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
  assert.match(tasksSource, /发送动作: \$\{isForwardMode \? '转发消息链接' : '发送消息规则'\}/)
  assert.match(tasksSource, /去重发送: \$\{skipIfLastMessageFromSelf \? '启用/)
  assert.match(taskConfigFormSource, /v-if="forms\.userChatActive\.messageActionMode === 'send_generated_text'" :span="8"/)
  assert.match(taskConfigFormSource, /message_mode: effectiveMessageMode/)
  assert.match(tasksSource, /\.\.\.\(isForwardMode \? \[\] : \[`内容模式:/)

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
  assert.match(tasksSource, /stripRuntimeFields\(fullTask\.config\)/)
})
