import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'
import { computed, reactive, ref } from 'vue'

const login = await readFile(new URL('../src/views/AccountLogin.vue', import.meta.url), 'utf8')
const imports = await readFile(new URL('../src/views/AccountImport.vue', import.meta.url), 'utf8')
const accounts = await readFile(new URL('../src/views/Accounts.vue', import.meta.url), 'utf8')
const requestId = 'f1d6ca5d-e611-4d83-996e-d144059a2b8a'

function body(source, name) {
  const match = source.match(new RegExp(`(?:async )?function ${name}\\([^]*?\\n\\}`))
  assert.ok(match, `未找到 ${name}`)
  return match[0]
}

function execute(source, names, bindings) {
  const js = ts.transpileModule(source, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
  return new Function(...Object.keys(bindings), `${js}\nreturn { ${names.join(', ')} };`)(...Object.values(bindings))
}

test('轻量登录未接受条款不能提交；同意后重试复用 UUID', () => {
  const values = {
    proxyStrategy: ref('warp_per_account'), proxyId: ref(null), availableWarpPoolCount: ref(0),
    warpStatus: ref({ available: true }), acceptWarpTerms: ref(false), warpRequestId: ref(''),
    crypto: { randomUUID: () => requestId }, computed, ElMessage: { warning() {} },
  }
  const selection = login.match(/const proxySelectionInvalid = computed\([^]*?\n\)/)[0]
  const state = execute(`${selection}\n${body(login, 'ensureProxySelected')}\n${body(login, 'selectedProxyPayload')}`,
    ['ensureProxySelected', 'selectedProxyPayload'], values)
  assert.equal(state.ensureProxySelected(), false)
  values.acceptWarpTerms.value = true
  assert.equal(state.ensureProxySelected(), true)
  assert.deepEqual(state.selectedProxyPayload(), {
    proxyStrategy: 'warp_per_account', proxyId: null, acceptWarpTerms: true, warpRequestId: requestId,
  })
  assert.equal(state.selectedProxyPayload().warpRequestId, requestId)
  values.proxyStrategy.value = 'direct'
  assert.equal(state.selectedProxyPayload().acceptWarpTerms, false)
  assert.equal(state.selectedProxyPayload().warpRequestId, null)
  for (const name of ['next', 'startQrLogin']) assert.match(body(login, name), /\.\.\.selectedProxyPayload\(\)/)
})

test('三种导入载荷携带条款和稳定 UUID，未接受时拒绝提交', async () => {
  const values = {
    proxyStrategy: ref('warp_per_account'), proxyId: ref(null), availableWarpPoolCount: ref(0),
    warpCreateAvailable: ref(true), acceptWarpTerms: ref(false), warpRequestId: ref(''), warpRequestKind: ref(''),
    isPerAccountProxyBatch: ref(false), perAccountProxyCount: ref(0), perAccountProxyLimitExceeded: ref(false),
    crypto: { randomUUID: () => requestId }, computed, ElMessage: { warning() {} }, warpStatus: ref({ available: true }),
    busy: ref(false), ensureTelegramApiConfigured: () => true, sessionString: ref('secret-session'),
    importCategoryId: ref(null), deviceProfileKey: ref('random'), importingString: ref(false),
    panelApi: { importAccountsStringSession: async (payload) => { received.push(payload); throw new Error('timeout') } },
  }
  const received = []
  const selection = imports.match(/const proxySelectionInvalid = computed\([^]*?\n\)/)[0]
  const source = `let importOperationToken = 0;\n${selection}\n${['ensureProxySelected', 'ensureWarpRequestId', 'appendProxyFields', 'appendZipProxyFields', 'importStringSession'].map((name) => body(imports, name)).join('\n')}`
  const state = execute(source, ['ensureProxySelected', 'appendProxyFields', 'appendZipProxyFields', 'importStringSession'], values)
  await state.importStringSession()
  assert.equal(received.length, 0)
  values.acceptWarpTerms.value = true
  const forms = [new FormData(), new FormData()]
  state.appendProxyFields(forms[0], 'warp_per_account', null)
  state.appendZipProxyFields(forms[1], 'warp_per_account', null, '')
  for (const form of forms) {
    assert.equal(form.get('acceptWarpTerms'), 'true')
    assert.equal(form.get('warpRequestId'), requestId)
  }
  await assert.rejects(state.importStringSession(), /timeout/)
  await assert.rejects(state.importStringSession(), /timeout/)
  assert.equal(received.length, 2)
  assert.equal(received[0].acceptWarpTerms, true)
  assert.equal(received[0].warpRequestId, requestId)
  assert.equal(received[1].warpRequestId, requestId)
  const direct = new FormData()
  state.appendProxyFields(direct, 'direct', null)
  assert.equal(direct.has('acceptWarpTerms'), false)
})

for (const accountIds of [[11], [11, 12]]) {
  test(`${accountIds.length} 个账号创建轻量出口必须接受条款且重试保持 UUID`, async () => {
    const received = []
    const fail = async (...args) => { received.push(args); throw new Error('timeout') }
    const dialog = reactive({ running: false, accountIds, strategy: 'warp_per_account', proxyId: null,
      expectedProxyId: 0, expectedUseGlobalProxy: false, acceptWarpTerms: false, warpRequestId: requestId })
    const state = execute(`let accountProxyOperationToken = 0;\n${body(accounts, 'saveAccountProxy')}`, ['saveAccountProxy'], {
      proxyDialog: dialog, warpAvailable: ref(true), warpStatus: ref({ available: true }), proxies: ref([]),
      panelApi: { setAccountProxy: fail, batchSetAccountProxy: fail }, ElMessage: { warning() {} },
    })
    await state.saveAccountProxy()
    assert.equal(received.length, 0)
    dialog.acceptWarpTerms = true
    await assert.rejects(state.saveAccountProxy(), /timeout/)
    await assert.rejects(state.saveAccountProxy(), /timeout/)
    assert.equal(received.length, 2)
    for (const [, payload] of received) {
      assert.equal(payload.acceptWarpTerms, true)
      assert.equal(payload.warpRequestId, requestId)
    }
    assert.equal(dialog.running, false)
  })
}

test('切换导入类型分配新 UUID，同类型失败重试保留原 UUID', () => {
  let count = 0
  const state = execute(body(imports, 'ensureWarpRequestId'), ['ensureWarpRequestId'], {
    warpRequestKind: ref(''), warpRequestId: ref(''), crypto: { randomUUID: () => `uuid-${++count}` },
  })
  assert.equal(state.ensureWarpRequestId('zip'), 'uuid-1')
  assert.equal(state.ensureWarpRequestId('zip'), 'uuid-1')
  assert.equal(state.ensureWarpRequestId('session-files'), 'uuid-2')
  assert.equal(state.ensureWarpRequestId('string-session'), 'uuid-3')
})

test('创建可用性取轻量运行器，登录和导入的已有代理仍排除受管轻量出口', () => {
  for (const source of [login, imports, accounts]) {
    assert.match(source, /panelApi\.wgcfStatus\(\)/)
    assert.doesNotMatch(source, /panelApi\.warpStatus\(\)|warpStatus\.value\.dockerAvailable/)
  }
  for (const source of [login, imports]) assert.match(source, /filter\(\(proxy\) => !proxy\.managedWgcfProfile\)/)
})
