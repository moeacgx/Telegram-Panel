import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const sourceUrl = new URL('../src/views/Settings.vue', import.meta.url)
const source = await readFile(sourceUrl, 'utf8')

test('批量重试设置说明群聊活跃任务的瞬时与永久错误边界', () => {
  assert.match(source, /失败自动重试/)
  assert.match(source, /群聊活跃任务会重试连接取消、超时和失效目标/)
  assert.match(source, /权限、Session 与风控错误不会重试/)
  assert.match(source, /<el-input-number v-model="batch\.maxRetries" :min="1" :max="5"/)
})

test('默认批量操作间隔最高允许 60000ms', () => {
  assert.match(source, /v-model="batch\.defaultDelayMs" :min="1000" :max="60000"/)
  assert.match(source, /最高 60000ms/)
})
