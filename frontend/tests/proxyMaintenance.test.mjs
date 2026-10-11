import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'
import { ref } from 'vue'

const proxiesSource = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
const panelApiSource = await readFile(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

test('代理列表支持对勾选项执行批量删除', () => {
  assert.match(proxiesSource, />\s*批量删除/)
  assert.match(proxiesSource, /selectedProxies\.value\.map\(\(proxy\) => proxy\.id\)/)
  assert.match(proxiesSource, /await deleteProxyItem\(current\)/)
  assert.match(panelApiSource, /deleteWgcfProfile:/)
  assert.match(panelApiSource, /\/proxies\/batch\/delete/)
})

test('批量删除展示逐项失败且保留被占用代理', () => {
  assert.match(typesSource, /interface ProxyBatchResult/)
  assert.match(proxiesSource, /deleteFailures\.value\.push/)
  assert.match(proxiesSource, /v-for="failure in deleteFailures"/)
  assert.match(proxiesSource, /占用中的项目会保留/)
})

function controller(api = {}) {
  const methods = ['proxyUsageCount', 'proxyIsUsed', 'proxyDeleteReason', 'deleteErrorMessage', 'deleteProxyItem', 'isSelectableProxy', 'removeSelectedProxies', 'removeProxy']
  const script = methods.map((name) => proxiesSource.match(new RegExp(`(?:async )?function ${name}\\([^]*?\\n\\}`))[0]).join('\n')
  const compiled = ts.transpileModule(script, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
  const calls = []
  const dependencies = { deletingProfiles: new Set(), deletingIds: new Set(), wgcfUnavailable: () => false,
    selectedProxies: ref([]), proxyRows: ref([]), batchDeleting: ref(false), deleteFailures: ref([]),
    extractApiErrorMessage: (data) => data?.message || '',
    ElMessageBox: { confirm: async () => {} }, ElMessage: { warning: () => {}, success: () => {}, error: () => {} },
    panelApi: { deleteWgcfProfile: async (id) => { calls.push(['wgcf', id]); return { success: true } },
      deleteProxy: async (id) => { calls.push(['ordinary', id]); return { success: true } }, ...api },
    loadProxies: async () => {}, loadProxyCategories: async () => {}, wgcfWarpPanel: ref(null), proxyTableRef: ref(null) }
  const state = new Function(...Object.keys(dependencies), `${compiled};return {${methods.join(',')}}`)(...Object.values(dependencies))
  return { ...state, ...dependencies, calls }
}
const ordinary = { id: 3, rowKey: 'proxy:3', name: '普通代理', kind: 'manual' }
const managed = { id: 4, rowKey: 'wgcf:p4', name: '轻量出口', kind: 'wireguard_warp', managedWgcfProfile: 'p4', wgcfProfile: { phase: 'ready', desired: true } }

test('删除按真实身份路由，未落库失败档案可删除且不先停止', async () => {
  const state = controller()
  await state.deleteProxyItem(ordinary)
  await state.deleteProxyItem(managed)
  await state.deleteProxyItem({ ...managed, id: 0, managedWgcfProfile: 'failed', wgcfProfile: { phase: 'failed' } })
  assert.deepEqual(state.calls, [['ordinary', 3], ['wgcf', 'p4'], ['wgcf', 'failed']])
  assert.equal(state.deletingProfiles.size, 0)
})

test('全局代理即使零用户以及绑定中或创建中的出口禁止删除', async () => {
  const state = controller()
  for (const proxy of [{ ...managed, isGlobal: true }, { ...managed, usageCount: 2 }, { ...managed, accountCount: 1 }, { ...managed, wgcfProfile: { phase: 'creating' } }]) {
    await assert.rejects(state.deleteProxyItem(proxy))
  }
  assert.deepEqual(state.calls, [])
})

test('混合批量删除逐项继续，所有失败有名称和原因且当前状态覆盖旧选择', async () => {
  const state = controller({ deleteProxy: async () => { throw new Error('请求超时') } })
  const stale = { ...managed, rowKey: 'wgcf:occupied', managedWgcfProfile: 'occupied', name: '已占用' }
  state.selectedProxies.value = [ordinary, managed, stale]
  state.proxyRows.value = [ordinary, managed, { ...stale, isGlobal: true }]
  await state.removeSelectedProxies()
  assert.deepEqual(state.calls, [['wgcf', 'p4']])
  assert.equal(state.deleteFailures.value.length, 2)
  assert.match(state.deleteFailures.value[0], /普通代理.*请求超时/)
  assert.match(state.deleteFailures.value[1], /已占用.*全局/)
  assert.equal(state.batchDeleting.value, false)
})

test('后端占用原因进入批删失败记录，单删后清除保留选择', async () => {
  const state = controller({ deleteWgcfProfile: async () => { throw Object.assign(new Error('HTTP 409'), { response: { data: { message: '首次连接仍在占用出口' } } }) } })
  state.selectedProxies.value = [managed]
  state.proxyRows.value = [managed]
  await state.removeSelectedProxies()
  assert.match(state.deleteFailures.value[0], /首次连接仍在占用/)
  let cleared = false
  state.proxyTableRef.value = { clearSelection: () => { cleared = true } }
  state.selectedProxies.value = [ordinary]
  await state.removeProxy(ordinary)
  assert.equal(cleared, true)
  assert.deepEqual(state.selectedProxies.value, [])
})
