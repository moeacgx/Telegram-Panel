import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')
const panelSource = await readFile(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
const accountsSource = await readFile(new URL('../src/views/Accounts.vue', import.meta.url), 'utf8')
const exportSource = await readFile(new URL('../../src/TelegramPanel.Web/Services/AccountExportService.cs', import.meta.url), 'utf8')
const deviceProfilesSource = await readFile(new URL('../src/views/TelegramDeviceProfiles.vue', import.meta.url), 'utf8')
const importSource = await readFile(new URL('../src/views/AccountImport.vue', import.meta.url), 'utf8')
const loginSource = await readFile(new URL('../src/views/AccountLogin.vue', import.meta.url), 'utf8')

test('在线设备授权 hash 使用字符串避免 64 位精度丢失', () => {
  assert.match(typesSource, /export interface TelegramAuthorization \{[\s\S]*hash: string/)
  assert.match(panelSource, /kickDevice: \(id: number, hash: string\)/)
  assert.match(panelSource, /encodeURIComponent\(hash\).*\/kick/)
})

test('踢出设备失败时显示失败结果，成功后立即移除并刷新设备列表', () => {
  assert.match(accountsSource, /const result = await panelApi\.kickDevice\(listDialog\.accountId, device\.hash\)/)
  assert.match(accountsSource, /if \(!result\.success\) \{[\s\S]*踢出设备失败/)
  assert.match(accountsSource, /listDialog\.devices = listDialog\.devices\.filter\(\(item\) => !removedHashes\.has\(item\.hash\)\)/)
  assert.match(accountsSource, /await refreshDevicesAfterKick\(removedHashes\)/)
})

test('账号详情清空设备画像时提交空字符串', () => {
  assert.match(accountsSource, /<el-option label="跟随系统默认" value="" \/>/)
  assert.match(accountsSource, /deviceProfileKey: details\.form\.deviceProfileKey,/)
  assert.doesNotMatch(accountsSource, /deviceProfileKey: details\.form\.deviceProfileKey \|\| null/)
})

test('设备指纹页只展示画像目录不展示 API 状态', () => {
  assert.match(deviceProfilesSource, /<template #header>设备指纹目录<\/template>/)
  assert.match(deviceProfilesSource, /保存默认画像/)
  assert.match(deviceProfilesSource, /<el-option label="随机设备指纹" value="random" \/>[\s\S]*v-for="profile in deviceProfiles"/)
  assert.match(importSource, /<el-option label="随机设备指纹" value="random" \/>[\s\S]*v-for="profile in deviceProfiles"/)
  assert.match(loginSource, /<el-option label="随机设备指纹" value="random" \/>[\s\S]*v-for="profile in deviceProfiles"/)
  assert.match(accountsSource, /<el-option label="随机设备指纹" value="random" \/>[\s\S]*v-for="profileOption in deviceProfiles"/)
  assert.match(deviceProfilesSource, /const requestedDefaultKey = defaultDeviceProfileKey\.value[\s\S]*defaultDeviceProfileKey: requestedDefaultKey[\s\S]*defaultDeviceProfileKey\.value = requestedDefaultKey/)
  assert.doesNotMatch(deviceProfilesSource, /Telegram API 状态/)
  assert.doesNotMatch(deviceProfilesSource, /去系统设置/)
  assert.doesNotMatch(deviceProfilesSource, /effectiveApiSource/)
})

test('导出独立 session 注入当前授权的设备画像', () => {
  assert.match(exportSource, /Account_GetAuthorizations\(\)/)
  assert.match(exportSource, /Authorization\.Flags\.current/)
  assert.match(exportSource, /ForCurrentAuthorization\(/)
  assert.match(exportSource, /"app_version" or "device_model" or "system_version"/) 
})
