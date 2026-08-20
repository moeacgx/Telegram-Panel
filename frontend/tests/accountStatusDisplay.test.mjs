import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const statusSource = await readFile(new URL('../src/utils/telegramStatus.ts', import.meta.url), 'utf8')
const accountsSource = await readFile(new URL('../src/views/Accounts.vue', import.meta.url), 'utf8')
const accountImportSource = await readFile(new URL('../src/views/AccountImport.vue', import.meta.url), 'utf8')
const accountLoginSource = await readFile(new URL('../src/views/AccountLogin.vue', import.meta.url), 'utf8')
const settingsSource = await readFile(new URL('../src/views/Settings.vue', import.meta.url), 'utf8')


test('临时 Telegram 网络错误与账号失效分开展示', () => {
  assert.match(statusSource, /'连接失败'/)
  assert.match(statusSource, /'请求超时'/)
  assert.match(statusSource, /'刷新失败'/)
  assert.match(accountsSource, /isTransientTelegramStatus\(row\.telegramStatusSummary\)\) return '连接异常'/)
  assert.match(accountsSource, /isInconclusiveTelegramStatus\(row\.telegramStatusSummary\)\) return 'warning'/)
})

test('账号导入页复用同一状态分类', () => {
  assert.match(accountImportSource, /telegramStatusTagType\(row\)/)
  assert.match(accountImportSource, /isTransientTelegramStatus\(row\.telegramStatusSummary\)\) return '连接异常'/)
  assert.match(accountImportSource, /isInconclusiveTelegramStatus\(row\.telegramStatusSummary\)\) return 'warning'/)
})

test('不确定检测结果不会显示为账号失效', () => {
  assert.match(statusSource, /'创建频道探测失败'/)
  assert.match(statusSource, /'无法获取账号资料'/)
  assert.match(accountsSource, /isInconclusiveTelegramStatus\(row\.telegramStatusSummary\)\) return '检测异常'/)
  assert.match(accountImportSource, /isInconclusiveTelegramStatus\(row\.telegramStatusSummary\)\) return '检测异常'/)
})

test('导入页只展示已导入账号并引导去账号列表操作', () => {
  assert.match(accountImportSource, /已导入账号（仅展示）/)
  assert.match(accountImportSource, /去账号列表操作/)
  assert.match(accountImportSource, /导入页不再提供批量操作/)
  assert.doesNotMatch(accountImportSource, /<BatchChatMembershipDialog/)
  assert.doesNotMatch(accountImportSource, /batchRefreshStatus/)
  assert.doesNotMatch(accountImportSource, /batchKickDevices/)
  assert.doesNotMatch(accountImportSource, /handleBatchCommand/)
})

test('导入和登录页使用服务端有效 API 状态', () => {
  for (const source of [accountImportSource, accountLoginSource]) {
    assert.match(source, /settings\.telegram\.effectiveApiId \|\| settings\.system\.effectiveApiId/)
    assert.match(source, /typeof settings\.telegram\.hasUsableApi === 'boolean'/)
    assert.match(source, /\? settings\.telegram\.hasUsableApi/)
  }
})

test('系统设置内管理 Telegram API 池和内置官方 API', () => {
  assert.match(settingsSource, /内置官方 API 固定在最顶上/)
  assert.match(settingsSource, /telegram\.officialApiEnabled/)
  assert.match(settingsSource, /profiles: telegram\.profiles/)
  assert.doesNotMatch(settingsSource, /改用内置官方 API/)
})
