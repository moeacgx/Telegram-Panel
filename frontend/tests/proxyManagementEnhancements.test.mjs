import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const viewSource = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
const apiSource = await readFile(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

test('全局代理只支持普通代理、Resin 和外部 WireGuard WARP', () => {
  assert.match(typesSource, /sourceMode: GlobalProxySourceMode/)
  assert.match(typesSource, /proxyId\?: number \| null/)
  assert.match(viewSource, /value="existing">从已有代理选择/)
  assert.match(viewSource, /v-model="globalProxyDialog\.form\.proxyId"/)
  assert.match(viewSource, /kind: 'wireguard_warp'.*label: '外部 WireGuard WARP'/s)
  assert.match(viewSource, /kind: 'manual'.*label: '普通代理'/s)
  assert.match(viewSource, /kind: 'resin'.*label: 'Resin 动态代理'/s)
  assert.doesNotMatch(viewSource, /kind: 'warp'.*label: '受管 WARP'/s)
  assert.doesNotMatch(viewSource, /openWarpCreateForGlobal\(\)/)
  assert.match(viewSource, /kind: 'wireguard_warp'[^\n]*!proxy\.managedWgcfProfile/)
  assert.match(viewSource, /openProxyCreateForGlobal\('wireguard_warp'\)/)
  assert.match(viewSource, /openProxyCreateForGlobal\('resin'\)/)
})

test('保存已有代理模式只提交代理引用并保留手动模式', () => {
  assert.match(viewSource, /sourceMode: form\.sourceMode/)
  assert.match(viewSource, /proxyId: form\.sourceMode === 'existing' \? form\.proxyId : null/)
  assert.match(viewSource, /所选代理已停用，请先启用或选择其他代理/)
  assert.match(viewSource, /账号专属代理优先/)
})

test('代理列表支持使用状态、分类筛选和多选批量设置', () => {
  assert.match(viewSource, /name="all"/)
  assert.match(viewSource, /name="used"/)
  assert.match(viewSource, /name="unused"/)
  assert.match(viewSource, /type="selection"[^>]*:reserve-selection="true"/)
  assert.match(viewSource, /@selection-change="onProxySelectionChange"/)
  assert.match(viewSource, /v-model="categoryFilter"/)
  assert.match(viewSource, /v-model="proxyDialog\.form\.categoryId"/)
  assert.match(viewSource, /categoryId: proxy\.category\?\.id \?\? null/)
  assert.match(viewSource, /proxy\.isInUse \?\? proxyUsageCount\(proxy\) > 0/)
  assert.match(viewSource, /panelApi\.batchSetProxyCategory\(proxyIds, categoryId\)/)
  assert.match(viewSource, /openCategoryManager/)
})

test('代理分类 API 覆盖查询、增删改和批量分配', () => {
  assert.match(apiSource, /get<ProxyCategory\[\]>\('\/proxy-categories'/)
  assert.match(apiSource, /post<ProxyCategory>\('\/proxy-categories'/)
  assert.match(apiSource, /put<ProxyCategory>\(`\/proxy-categories\/\$\{id\}`/)
  assert.match(apiSource, /delete<OperationResult>\(`\/proxy-categories\/\$\{id\}`/)
  assert.match(apiSource, /post<OperationResult>\('\/proxies\/batch\/category'/)
})

test('出口检测明确展示 IP、地区和 ISP', () => {
  assert.match(viewSource, /label="公网出口 IP"/)
  assert.match(viewSource, /egress-field-label">地区/)
  assert.match(viewSource, /egress-field-label">ISP/)
  assert.match(viewSource, /row\.egressCountry/)
  assert.match(viewSource, /row\.egressCity/)
  assert.match(viewSource, /row\.egressIsp/)
  assert.match(viewSource, /ISP \$\{isp\}/)
})
