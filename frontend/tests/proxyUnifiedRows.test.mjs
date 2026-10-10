import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'
import { computed, ref } from 'vue'

const utility = await readFile(new URL('../src/utils/proxyRows.ts', import.meta.url), 'utf8')
const view = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
const controller = await readFile(new URL('../src/components/WgcfWarpPanel.vue', import.meta.url), 'utf8')
const js = ts.transpileModule(utility, { compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2020 } }).outputText
const { mergeProxyRows, countAvailableWgcfPool } = await import(`data:text/javascript;base64,${Buffer.from(js).toString('base64')}`)

const profile = (extra = {}) => ({ profile: 'web-one', name: '独立出口', phase: 'ready', registered: true,
  generated: true, desired: true, runtime: 'listening', proxyId: 12, accountCount: 0,
  testStatus: 'ok', egressIp: '1.2.3.4', error: null, ...extra })
const proxy = (extra = {}) => ({ id: 12, name: '独立出口', kind: 'wireguard_warp', protocol: 'socks5',
  managedWgcfProfile: 'web-one', host: '127.0.0.1', port: 1080, isEnabled: true, testStatus: 'ok',
  egressIp: '1.2.3.4', accountCount: 0, isGlobal: false, createdAtUtc: '', updatedAtUtc: '', ...extra })

test('尚未落库的创建中与失败档案都在统一表内，并使用独立稳定行键', () => {
  const rows = mergeProxyRows([], [profile({ proxyId: null, phase: 'creating' }), profile({ profile: 'web-two', proxyId: null, phase: 'failed', error: '注册失败' })])
  assert.equal(rows.length, 2)
  assert.deepEqual(rows.map((row) => row.rowKey), ['wgcf:web-one', 'wgcf:web-two'])
  assert.deepEqual(rows.map((row) => row.wgcfProfile.phase), ['creating', 'failed'])
  assert.equal(rows[1].lastError, '注册失败')
  assert.equal(rows[0].managedWgcfProfile, 'web-one')
})

test('档案落库前后保持同一行键，数据库记录与运行档案不重复计数', () => {
  const pending = mergeProxyRows([], [profile({ proxyId: null, phase: 'creating' })])[0]
  const rows = mergeProxyRows([proxy({ category: { id: 3, name: '专用' } })], [profile()])
  assert.equal(rows.length, 1)
  assert.equal(rows[0].rowKey, pending.rowKey)
  assert.equal(rows[0].id, 12)
  assert.equal(rows[0].host, '127.0.0.1')
  assert.equal(rows[0].category.id, 3)
})

test('数据库快照尚未带档案标识时也按 proxyId 合并，重复档案不会重复显示', () => {
  const rows = mergeProxyRows([proxy({ managedWgcfProfile: null })], [profile(), profile({ phase: 'failed' })])
  assert.equal(rows.length, 1)
  assert.equal(rows[0].wgcfProfile.phase, 'failed')
  assert.equal(rows[0].managedWgcfProfile, 'web-one')
})

test('运行档案暂时缺失时保留代理且保护管理身份，绑定数采用保守并集', () => {
  const orphan = mergeProxyRows([proxy()], [])[0]
  assert.equal(orphan.managedWgcfProfile, 'web-one')
  assert.equal(orphan.wgcfProfile, undefined)
  const bound = mergeProxyRows([proxy({ accountCount: 1 })], [profile()])[0]
  assert.equal(bound.wgcfProfile.accountCount, 1)
  assert.equal(bound.isInUse, true)
})

test('使用状态计数与分类筛选均基于合并后的统一行', () => {
  const proxyRows = ref(mergeProxyRows([
    proxy({ accountCount: 1, category: { id: 3 } }),
    proxy({ id: 20, kind: 'manual', managedWgcfProfile: null }),
  ], [profile({ accountCount: 1 }), profile({ profile: 'web-pending', proxyId: null, phase: 'creating' })]))
  const usageFilter = ref('all')
  const categoryFilter = ref('all')
  const counts = view.match(/const usageCounts = computed\([^]*?\n\}\)/)[0]
  const filter = view.match(/const filteredProxies = computed\([^]*?\n\}\)\)/)[0]
  const helpers = ['proxyUsageCount', 'proxyIsUsed'].map((name) => view.match(new RegExp(`function ${name}\\([^]*?\\n\\}`))[0]).join('\n')
  const code = ts.transpileModule(`${helpers}\n${counts}\n${filter}`, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
  const state = new Function('computed', 'proxyRows', 'usageFilter', 'categoryFilter', `${code}\nreturn { usageCounts, filteredProxies }`)(computed, proxyRows, usageFilter, categoryFilter)
  assert.deepEqual(state.usageCounts.value, { all: 3, used: 1, unused: 2 })
  usageFilter.value = 'unused'
  assert.equal(state.filteredProxies.value.length, 2)
  categoryFilter.value = 3
  assert.equal(state.filteredProxies.value.length, 0)
  usageFilter.value = 'used'
  assert.equal(state.filteredProxies.value[0].id, 12)
})

test('轻量池只计算可独占的健康空闲出口，拒绝旧容器与占用出口', () => {
  const status = { available: true, reason: null, profiles: [profile()] }
  assert.equal(countAvailableWgcfPool([proxy()], status), 1)
  for (const extra of [{ managedWgcfProfile: null, kind: 'warp' }, { isEnabled: false }, { testStatus: 'failed' }, { egressIp: null }, { accountCount: 1 }, { usageCount: 1 }, { isGlobal: true }]) {
    assert.equal(countAvailableWgcfPool([proxy(extra)], status), 0)
  }
  for (const extra of [{ phase: 'failed' }, { desired: false }, { runtime: 'stopped' }, { accountCount: 1 }]) {
    assert.equal(countAvailableWgcfPool([proxy()], { ...status, profiles: [profile(extra)] }), 0)
  }
  assert.equal(countAvailableWgcfPool([proxy()], { ...status, available: false }), 0)
})

test('无独立轻量列表，统一表的动作调用档案控制器且待生成行不可批量编辑', () => {
  assert.doesNotMatch(controller, /<el-table|<section|<h2/)
  assert.match(view, /row-key="rowKey"/)
  for (const action of ['resume', 'start', 'stop', 'test']) assert.ok(view.includes(`operate(row.wgcfProfile, '${action}')`))
  assert.ok(view.includes('openBind(row.wgcfProfile)'))
  const fn = view.match(/function isSelectableProxy\([^]*?\n\}/)[0]
  const code = ts.transpileModule(fn, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
  const selectable = new Function(`${code}\nreturn isSelectableProxy`)()
  assert.equal(selectable(mergeProxyRows([], [profile({ proxyId: null })])[0]), false)
  assert.equal(selectable(proxy({ kind: 'warp', managedWgcfProfile: null })), false)
  assert.equal(selectable(proxy({ kind: 'manual', managedWgcfProfile: null })), true)
})
