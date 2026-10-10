import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'
import { computed, reactive, ref } from 'vue'

const component = await readFile(new URL('../src/components/WgcfWarpPanel.vue', import.meta.url), 'utf8').catch(() => '')
const proxiesSource = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')

function setup(overrides = {}) {
  assert.ok(component, '轻量 WARP 管理组件尚未实现')
  const script = component.match(/<script setup lang="ts">([\s\S]*?)<\/script>/)?.[1]
  assert.ok(script, '管理组件缺少脚本')
  const messages = []
  const events = []
  const api = {
    wgcfStatus: async () => ({ available: true, reason: null, profiles: [] }),
    accounts: async () => ({ items: [], total: 0 }),
    ...overrides,
  }
  const js = ts.transpileModule(script.replace(/^import[\s\S]*?from ['"][^'"]+['"]\s*$/gm, ''), {
    compilerOptions: { module: ts.ModuleKind.ESNext, target: ts.ScriptTarget.ES2020 },
  }).outputText.replace(/export \{\};?/, '')
  const factory = new Function('ref', 'reactive', 'computed', 'onMounted', 'onBeforeUnmount', 'defineEmits', 'panelApi', 'ElMessage', 'crypto', 'defineExpose',
    `${js}\nreturn {status, createDialog, bindDialog, profiles, busyProfiles, openCreate, createProfile, operate, canBind, openBind, bindAccount, loadStatus};`)
  const state = factory(ref, reactive, computed, () => {}, () => {}, () => (...args) => events.push(args), api,
    Object.fromEntries(['success', 'warning', 'error'].map((type) => [type, (message) => messages.push({ type, message })])),
    { randomUUID: () => '0efca5bd-b7b0-4a77-92b7-112c686ef591' }, () => {})
  state.status.value = { available: true, reason: null, profiles: [] }
  return { ...state, messages, events }
}

function profile(values = {}) {
  return { profile: 'wgcf-one', name: '测试出口', phase: 'ready', registered: true, generated: true,
    desired: true, runtime: 'listening', proxyId: 17, accountCount: 0, testStatus: 'ok', egressIp: '1.2.3.4', error: null, ...values }
}

test('环境不可用时禁止打开创建并且不提交注册', async () => {
  let calls = 0
  const state = setup({ createWgcfProfile: async () => { calls++; return profile() } })
  state.status.value.available = false
  state.openCreate()
  assert.equal(state.createDialog.visible, false)
  await state.createProfile()
  assert.equal(calls, 0)
})

test('未接受 WARP 条款时不能创建', async () => {
  let calls = 0
  const state = setup({ createWgcfProfile: async () => { calls++; return profile() } })
  state.openCreate()
  state.createDialog.name = '出口'
  await state.createProfile()
  assert.equal(calls, 0)
  assert.equal(state.createDialog.visible, true)
})

test('请求超时后重试保持同一 requestId，成功后显示后台创建', async () => {
  const requests = []
  const state = setup({ createWgcfProfile: async (payload) => {
    requests.push(payload)
    if (requests.length === 1) throw new Error('timeout')
    return profile({ phase: 'creating', proxyId: null })
  } })
  state.openCreate()
  state.createDialog.name = ' 出口 '
  state.createDialog.acceptTerms = true
  await state.createProfile()
  assert.equal(state.createDialog.visible, true)
  await state.createProfile()
  assert.deepEqual(requests, [
    { name: '出口', acceptTerms: true, requestId: '0efca5bd-b7b0-4a77-92b7-112c686ef591' },
    { name: '出口', acceptTerms: true, requestId: '0efca5bd-b7b0-4a77-92b7-112c686ef591' },
  ])
  assert.equal(state.createDialog.visible, false)
  assert.equal(state.profiles.value[0].phase, 'creating')
})

test('失败档案重试继续原档案而不重新注册', async () => {
  const calls = []
  const state = setup({ resumeWgcfProfile: async (id) => { calls.push(id); return profile({ phase: 'creating' }) } })
  await state.operate(profile({ phase: 'failed' }), 'resume')
  assert.deepEqual(calls, ['wgcf-one'])
})

test('只有联网检测成功且运行中的出口允许绑定', () => {
  const state = setup()
  assert.equal(state.canBind(profile()), true)
  for (const changes of [{ testStatus: 'failed' }, { egressIp: null }, { desired: false }, { runtime: 'unknown' }, { proxyId: null }, { phase: 'creating' }]) {
    assert.equal(state.canBind(profile(changes)), false)
  }
})

test('绑定失败保留对话框且不误报成功', async () => {
  const state = setup({ setAccountProxy: async () => ({ success: 0, failed: 1, items: [{ accountId: 123, success: false, error: '冲突', summary: '' }] }) })
  state.openBind(profile())
  state.bindDialog.accountId = 123
  state.bindDialog.accounts = [{ id: 123, proxy: { id: 9 }, useGlobalProxy: false, nickname: '账号', displayPhone: '123' }]
  await state.bindAccount()
  assert.equal(state.bindDialog.visible, true)
  assert.equal(state.messages.some((item) => item.type === 'success'), false)
})

test('单账号绑定携带当前代理编号并只绑定明确选择的账号', async () => {
  let received
  const state = setup({ setAccountProxy: async (...args) => { received = args; return { success: 1, failed: 0, items: [{ accountId: 123, success: true, summary: '已绑定' }] } } })
  state.openBind(profile())
  state.bindDialog.accountId = 123
  state.bindDialog.accounts = [{ id: 123, proxy: { id: 9 }, useGlobalProxy: false, nickname: '账号', displayPhone: '123' }]
  await state.bindAccount()
  assert.deepEqual(received, [123, { strategy: 'existing', proxyId: 17, expectedProxyId: 9, expectedUseGlobalProxy: false }])
  assert.equal(state.bindDialog.visible, false)
})

test('有绑定账号的档案禁止停止', async () => {
  let calls = 0
  const state = setup({ stopWgcfProfile: async () => { calls++; return profile() } })
  await state.operate(profile({ accountCount: 1 }), 'stop')
  assert.equal(calls, 0)
})

test('同一档案的操作不会重复提交', async () => {
  let calls = 0
  let finish
  const state = setup({ testWgcfProfile: () => { calls++; return new Promise((resolve) => { finish = resolve }) } })
  const pending = state.operate(profile(), 'test')
  await state.operate(profile(), 'test')
  assert.equal(calls, 1)
  finish(profile())
  await pending
  assert.equal(state.busyProfiles.has('wgcf-one'), false)
})

test('已持久化的同一请求也会关闭创建对话框', async () => {
  const state = setup({ createWgcfProfile: async () => profile() })
  state.openCreate()
  state.createDialog.name = '出口'
  state.createDialog.acceptTerms = true
  await state.createProfile()
  assert.equal(state.createDialog.visible, false)
  assert.equal(state.createDialog.running, false)
})

test('创建响应早于首次状态读取时仍会收口对话框', async () => {
  let complete
  const state = setup({ createWgcfProfile: () => new Promise((resolve) => { complete = resolve }) })
  state.openCreate()
  state.createDialog.name = '出口'
  state.createDialog.acceptTerms = true
  const pending = state.createProfile()
  // 模拟请求已经发出后，首轮状态读取尚未落地的窗口。
  state.status.value = null
  complete(profile())
  await pending
  assert.equal(state.createDialog.visible, false)
  assert.equal(state.profiles.value[0].profile, 'wgcf-one')
})

test('创建或操作结果替换状态档案，而不写入只读派生列表', () => {
  assert.match(component, /const current = status\.value \?\? \{ available: true, reason: null, profiles: \[\] \}/)
  assert.match(component, /status\.value = \{[\s\S]*?profiles: \[\.\.\.current\.profiles\.filter/)
  assert.doesNotMatch(component, /status\.value\.profiles\s*=/)
})

test('受管轻量出口不允许选入普通代理批量操作', () => {
  const match = proxiesSource.match(/function isSelectableProxy\([^)]*\) \{[\s\S]*?\n\}/)
  assert.ok(match, '缺少受管出口批量操作保护')
  const js = ts.transpileModule(`${match[0]}\nexport { isSelectableProxy }`, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
  const selectable = new Function(`${js.replace(/export \{.*?\};/, '')}\nreturn isSelectableProxy`)()
  assert.equal(selectable({ managedWgcfProfile: 'wgcf-one' }), false)
  assert.equal(selectable({ managedWgcfProfile: null }), true)
})

for (const [view, functionName] of [['AccountLogin', 'loadLoginProxyOptions'], ['AccountImport', 'loadProxies']]) {
  test(`${view} 代理选项排除受管轻量出口`, async () => {
    const source = await readFile(new URL(`../src/views/${view}.vue`, import.meta.url), 'utf8')
    const match = source.match(new RegExp(`async function ${functionName}\\([^)]*\\) \\{[\\s\\S]*?\\n\\}`))
    assert.ok(match)
    const js = ts.transpileModule(match[0], { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
    const proxies = ref([])
    const call = new Function('proxies', 'panelApi', 'availableWarpPoolCount', 'proxyStrategy', `${js}\nreturn ${functionName}`)(
      proxies, { proxies: async () => [{ id: 1, managedWgcfProfile: 'wgcf-one' }, { id: 2, managedWgcfProfile: null }] }, ref(1), ref('existing'))
    await call()
    assert.deepEqual(proxies.value.map((proxy) => proxy.id), [2])
  })
}
