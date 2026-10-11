import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'
import { computed, reactive } from 'vue'

const source = await readFile(new URL('../src/components/ProxyBoundAccountsDialog.vue', import.meta.url), 'utf8')
const account = { id: 10, displayNumber: 3, proxyId: 7, useGlobalProxy: false, phoneLastFour: '1234' }
const proxy = { id: 7, name: '原出口' }
const page = (items = [account], index = 1) => ({ items, total: items.length, page: index, pageSize: 20 })
function setup(api = {}) {
  const events = [], messages = []
  const script = source.match(/<script setup lang="ts">([\s\S]*?)<\/script>/)[1]
  const js = ts.transpileModule(script.replace(/^import.*$/gm, ''), { compilerOptions: { target: ts.ScriptTarget.ES2020, module: ts.ModuleKind.ESNext } }).outputText.replace(/export \{\};?/, '')
  let dispose
  const state = new Function('computed', 'reactive', 'onBeforeUnmount', 'defineEmits', 'defineExpose', 'panelApi', 'ElMessage', 'onBeforeRouteLeave',
    `${js}; return {dialog,eligibleTargets,canSubmit,open,loadAccounts,selectAccount,loadTargets,submit,beforeClose}`)(
      computed, reactive, (f) => { dispose = f }, () => (...args) => events.push(args), () => {},
      { proxyAccounts: async () => page(), proxies: async () => [], wgcfStatus: async () => ({ available: true, profiles: [] }), ...api },
      { success: (m) => messages.push(m) }, () => {})
  return { ...state, events, messages, dispose: () => dispose() }
}

test('点击已绑定账号按代理分页读取，未选择解绑方式不能提交', async () => {
  const reads = []
  const state = setup({ proxyAccounts: async (...args) => { reads.push(args); return page() } })
  await state.open(proxy)
  state.selectAccount(state.dialog.accounts[0])
  assert.deepEqual(reads, [[7, 1]])
  assert.equal(state.canSubmit.value, false)
  await state.loadAccounts(2)
  assert.deepEqual(reads[1], [7, 2])
  assert.equal(state.dialog.selected, null)
})

for (const strategy of ['direct', 'global', 'existing']) {
  test(`${strategy} 一次提交当前路由快照，成功后回读并刷新代理列表`, async () => {
    const calls = []
    let reads = 0
    const state = setup({
      proxyAccounts: async () => { reads++; return page(reads > 1 ? [] : [account]) },
      setAccountProxy: async (...args) => { calls.push(args); return { failed: 0, items: [{ accountId: 10, success: true }] } },
      proxies: async () => [{ id: 8, isEnabled: true, kind: 'manual' }],
    })
    await state.open(proxy)
    state.selectAccount(account)
    await state.loadTargets()
    state.dialog.strategy = strategy
    state.dialog.targetId = 8
    await state.submit()
    assert.deepEqual(calls, [[10, { strategy, proxyId: strategy === 'existing' ? 8 : null, expectedProxyId: 7, expectedUseGlobalProxy: false }]])
    assert.equal(reads, 2)
    assert.equal(state.dialog.accounts.length, 0)
    assert.equal(state.messages.length, 1)
    assert.equal(state.events.length, 1)
  })
}

test('目标代理排除自己、旧容器、停用及未就绪或占用的轻量出口', async () => {
  const state = setup()
  await state.open(proxy)
  state.dialog.targets = [proxy, { id: 8, kind: 'manual', isEnabled: true },
    { id: 9, kind: 'warp', isEnabled: true }, { id: 10, kind: 'manual', isEnabled: false },
    ...[11, 12, 13].map((id) => ({ id, kind: 'wireguard_warp', managedWgcfProfile: `p${id}`, isEnabled: true }))]
  state.dialog.runtime = { available: true, profiles: [11, 12, 13].map((id) => ({ profile: `p${id}`, proxyId: id, poolEligible: id !== 12,
    phase: 'ready', desired: true, runtime: 'listening', testStatus: 'ok', egressIp: '1.2.3.4', accountCount: id === 13 ? 1 : 0 })) }
  assert.deepEqual(state.eligibleTargets.value.map((p) => p.id), [8, 11])
  state.dialog.runtime.available = false
  assert.deepEqual(state.eligibleTargets.value.map((p) => p.id), [8])
})

test('提交期间禁止重复提交和关闭；失败结果不显示成功并丢弃旧选择', async () => {
  let finish, calls = 0, closed = false
  const state = setup({ setAccountProxy: () => { calls++; return new Promise((r) => { finish = r }) } })
  await state.open(proxy)
  state.selectAccount(account)
  state.dialog.strategy = 'direct'
  const running = state.submit()
  state.beforeClose(() => { closed = true })
  await state.submit()
  assert.equal(closed, false)
  assert.equal(calls, 1)
  finish({ failed: 1, items: [{ accountId: 10, success: false, error: '绑定已变化' }] })
  await running
  assert.equal(state.messages.length, 0)
  assert.match(state.dialog.error, /绑定已变化/)
  assert.equal(state.dialog.selected, null)
  assert.equal(state.canSubmit.value, false)
})

test('响应丢失后重新读取绑定，不自动重试修改', async () => {
  let calls = 0, reads = 0
  const state = setup({ proxyAccounts: async () => { reads++; return page() }, setAccountProxy: async () => { calls++; throw new Error('timeout') } })
  await state.open(proxy)
  state.selectAccount(account)
  state.dialog.strategy = 'global'
  await state.submit()
  await state.submit()
  assert.equal(calls, 1)
  assert.equal(reads, 2)
  assert.equal(state.messages.length, 0)
  assert.match(state.dialog.error, /重新读取/)
})

test('打开另一代理时旧查询结果不能覆盖当前账号列表', async () => {
  let finish
  const state = setup({ proxyAccounts: (id) => id === 7 ? new Promise((r) => { finish = r }) : Promise.resolve(page([{ ...account, proxyId: 8 }])) })
  const first = state.open(proxy)
  await state.open({ id: 8, name: '另一个出口' })
  finish(page())
  await first
  assert.equal(state.dialog.accounts[0].proxyId, 8)
})

test('运行器状态请求失败时仍可选择普通代理迁移', async () => {
  const state = setup({ proxies: async () => [{ id: 8, kind: 'manual', isEnabled: true }, { id: 9, kind: 'wireguard_warp', isEnabled: true, managedWgcfProfile: 'p9' }],
    wgcfStatus: async () => { throw new Error('WARP 故障') } })
  await state.open(proxy)
  state.selectAccount(account)
  await state.loadTargets()
  state.dialog.strategy = 'existing'
  state.dialog.targetId = 8
  assert.deepEqual(state.eligibleTargets.value.map((p) => p.id), [8])
  assert.equal(state.canSubmit.value, true)
})

test('解绑末页最后一个账号后自动回到有效页', async () => {
  const reads = []
  const state = setup({ proxyAccounts: async (id, index) => { reads.push(index); return { items: index === 2 ? [] : [account], total: 20, page: index, pageSize: 20 } } })
  await state.open(proxy)
  await state.loadAccounts(2)
  assert.deepEqual(reads, [1, 2, 1])
  assert.equal(state.dialog.page, 1)
  assert.equal(state.dialog.accounts.length, 1)
})
