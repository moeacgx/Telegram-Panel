import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const tasksSource = await readFile(new URL('../src/views/Tasks.vue', import.meta.url), 'utf8')

test('自动创建私密频道或群组任务展示最近失败原因', () => {
  assert.match(tasksSource, /buildChannelGroupAutomationFailureLines\(obj\)/)
  assert.match(tasksSource, /Array\.isArray\(obj\.recent_failures\)/)
  assert.match(tasksSource, /账号 #\$\{accountId\}/)
  assert.match(tasksSource, /最近失败:/)
  assert.match(tasksSource, /\.slice\(-20\)/)
})

test('批量加群订阅任务展示失败账号目标和原因', () => {
  assert.match(tasksSource, /taskType === 'user_join_subscribe'\) return buildUserJoinSubscribeDetails\(obj\)/)
  assert.match(tasksSource, /function buildUserJoinSubscribeDetails\(obj: Record<string, any>\)/)
  assert.match(tasksSource, /失败记录: \$\{obj\.failures\.length\} 项/)
  assert.match(tasksSource, /账号 #\$\{accountId \|\| '-'\} -> \$\{target\}：\$\{reason\}/)
})


test('重新运行任务会清除旧的运行态失败记录', () => {
  assert.match(tasksSource, /config:\s*fullTask\.config\s*\?\s*stripRuntimeFields\(fullTask\.config\)\s*:\s*null/)
  assert.match(tasksSource, /delete obj\.recent_failures/)
  assert.match(tasksSource, /delete obj\.failures/)
})
