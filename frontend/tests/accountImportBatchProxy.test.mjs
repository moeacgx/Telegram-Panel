import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const source = await readFile(new URL('../src/views/AccountImport.vue', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

function sourceSection(startMarker, endMarker) {
  const start = source.indexOf(startMarker)
  const end = source.indexOf(endMarker, start)
  assert.notEqual(start, -1, `未找到开始标记：${startMarker}`)
  assert.notEqual(end, -1, `未找到结束标记：${endMarker}`)
  return source.slice(start, end)
}

test('逐账号批量代理仅扩展 Zip 导入策略', () => {
  assert.match(typesSource, /export type AccountProxyStrategy = 'direct' \| 'global' \| 'existing' \| 'warp_per_account' \| 'warp_pool'/)
  assert.match(typesSource, /export type AccountImportProxyStrategy = Exclude<AccountProxyStrategy, 'warp_per_account'> \| 'warp_pool'/)
  assert.match(typesSource, /export type ZipImportProxyStrategy = AccountImportProxyStrategy \| 'proxy_per_account'/)
  assert.doesNotMatch(
    typesSource.match(/export type AccountProxyStrategy = .+/)?.[0] || '',
    /proxy_per_account/,
  )
  assert.match(source, /value="proxy_per_account">批量代理一对一/)
  assert.match(source, /批量代理一对一仅适用于 Zip 导入/)
})

test('账号导入移除独立 WARP 创建并只自动分配已有 WARP', () => {
  assert.doesNotMatch(source, /value="warp_per_account"|每账号独立 WARP/)
  assert.match(source, /value="warp_pool"[^>]*>自动分配已有 WARP/)
  assert.match(source, /proxy\.kind === 'warp'[\s\S]*proxy\.isEnabled[\s\S]*proxy\.warpRuntimeStatus === 'active'/)
  assert.match(source, /不会创建新容器/)
  assert.doesNotMatch(source, /panelApi\.warpStatus\(\)/)
})

test('有效代理计数忽略空行和注释并保留重复槽位', () => {
  const match = source.match(/function countEffectiveProxyLines\(text: string\) \{([\s\S]*?)\n\}/)
  assert.ok(match, '未找到有效代理计数函数')
  const factory = new Function(
    `${`function countEffectiveProxyLines(text) {${match[1]}\n}`}\nreturn countEffectiveProxyLines`,
  )
  const countEffectiveProxyLines = factory()

  assert.equal(countEffectiveProxyLines(''), 0)
  assert.equal(countEffectiveProxyLines('  # 注释\r\n\r\nhttp://a:1\r\n http://a:1 \n socks5://b:2'), 3)
})

test('Zip 请求冻结并提交代理文本，成功后只清空未变化的凭据', () => {
  const appendBody = sourceSection('function appendZipProxyFields(', 'async function importZip()')
  const importBody = sourceSection('async function importZip()', 'async function importSessionFiles()')

  assert.match(appendBody, /form\.append\('proxyStrategy', strategy\)/)
  assert.match(appendBody, /strategy === 'proxy_per_account'[\s\S]*form\.append\('proxyText', selectedProxyText\)/)
  assert.match(importBody, /const selectedProxyText = perAccountProxyText\.value/)
  assert.match(importBody, /appendZipProxyFields\(form, selectedStrategy, selectedProxyId, selectedProxyText\)/)
  assert.match(
    importBody,
    /response = await panelApi\.importAccountsZip\(form\)[\s\S]*?catch \{[\s\S]*?return[\s\S]*?\}/,
  )
  assert.doesNotMatch(importBody, /console\.|throw\s/)
  assert.match(
    importBody,
    /selectedStrategy === 'proxy_per_account' && perAccountProxyText\.value === selectedProxyText[\s\S]*perAccountProxyText\.value = ''/,
  )
  assert.ok(
    importBody.indexOf('panelApi.importAccountsZip') < importBody.indexOf("perAccountProxyText.value = ''"),
    '代理凭据只能在请求成功返回后清空',
  )
})

test('离开导入页面时清理批量代理凭据', () => {
  assert.match(source, /onBeforeUnmount\(\(\) => \{[\s\S]*perAccountProxyText\.value = ''[\s\S]*\}\)/)
})

test('批量模式禁用非 Zip 导入并明确首连和排序规则', () => {
  assert.match(source, /const sessionImportDisabled = computed\(\(\) =>[\s\S]*?isPerAccountProxyBatch\.value[\s\S]*?\n\)/)
  assert.match(source, /const stringImportDisabled = computed\(\(\) =>[\s\S]*?isPerAccountProxyBatch\.value[\s\S]*?\n\)/)
  assert.match(source, /const PER_ACCOUNT_PROXY_LIMIT = 100/)
  assert.match(source, /perAccountProxyCount\.value > PER_ACCOUNT_PROXY_LIMIT/)
  assert.match(source, /检测代理并导入 Zip/)
  assert.match(source, /账号按 Zip 内规范化相对路径稳定排序/)
  assert.match(source, /账号数与代理数必须完全一致/)
  assert.match(source, /全部成功后才新增代理并连接 Telegram/)
  assert.match(source, /账号首次请求即使用对应代理，不会先直连再绑定/)
})

test('导入结果展示代理分配元数据但不回显原始代理凭据', () => {
  const resultTemplate = sourceSection(
    '<el-card v-if="importResults.length"',
    '<el-card v-if="rows.length"',
  )
  for (const field of ['sourceKey', 'proxyLine', 'proxyId', 'proxyName', 'proxyEgressIp']) {
    assert.match(typesSource, new RegExp(`${field}\\?:`))
    assert.match(resultTemplate, new RegExp(`prop="${field}"`))
  }
  assert.doesNotMatch(resultTemplate, /perAccountProxyText|proxyText|password|用户名:密码/)
})
