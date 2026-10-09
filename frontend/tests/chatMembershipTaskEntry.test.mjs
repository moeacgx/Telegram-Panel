import test from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync } from 'node:fs'

test('账号加群任务使用专用入口并交由服务端规范配置和计数', async () => {
  const source = readFileSync(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
  const entry = source.slice(source.indexOf('  createChatMembershipTask:'), source.indexOf('  updateAccountProfile:'))
  const calls = []
  const expectedTask = { id: 42, taskType: 'user_join_subscribe', status: 'pending', total: 1 }
  const api = { post: async (...args) => { calls.push(args); return { data: expectedTask } } }
  const expression = entry.slice(entry.indexOf(':') + 1).trim().replace(/,$/, '')
    .replace(': CreateChatMembershipTaskRequest', '').replace('<BatchTask>', '')
  const createTask = new Function('api', `return (${expression})`)(api)
  const payload = { accountIds: [1, 1], operation: 'leave', links: ['@example', '@EXAMPLE'], delayMs: 2000 }

  assert.equal(await createTask(payload), expectedTask)
  assert.deepEqual(calls, [['/accounts/chat-membership/tasks', payload]])
})
