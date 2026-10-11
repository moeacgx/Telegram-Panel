import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'
import { reactive } from 'vue'

const source = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
function controller(rename = async () => ({ success: true })) {
  const names = ['openRename', 'closeRename', 'saveRename']
  const code = names.map(name => {
    const match = source.match(new RegExp(`(?:async )?function ${name}\\([^]*?\\n\\}`))
    assert.ok(match, `缺少 ${name}`)
    return match[0]
  }).join('\n')
  const calls = []
  const renameDialog = reactive({ visible: false, saving: false, id: 0, name: '', error: '' })
  const bindings = { renameDialog, deletingIds: new Set(), deletingProfiles: new Set(),
    panelApi: { renameProxy: async (...args) => { calls.push(args); return rename(...args) } },
    ElMessage: { success() {} }, extractApiErrorMessage: data => data?.message || '',
    loadProxies: async () => {}, wgcfWarpPanel: { value: { loadStatus: async () => {} } } }
  const js = ts.transpileModule(code, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
  return { ...new Function(...Object.keys(bindings), `${js};return {${names.join(',')}}`)(...Object.values(bindings)), ...bindings, calls }
}

for (const kind of ['manual', 'resin', 'wireguard_warp']) {
  test(`${kind} 重命名仅提交编号和名称，使用中的出口也可修改`, async () => {
    const c = controller()
    c.openRename({ id: 7, name: '原名称', kind, accountCount: 3, isGlobal: true, host: 'secret-host', password: 'secret' })
    assert.equal(c.renameDialog.visible, true)
    assert.equal(c.renameDialog.name, '原名称')
    c.renameDialog.name = ' 新名称 '
    await c.saveRename()
    assert.deepEqual(c.calls, [[7, { name: '新名称' }]])
    assert.equal(c.renameDialog.visible, false)
  })
}

test('空名称或超过80字符不会提交；未落库档案无法重命名', async () => {
  const c = controller()
  c.openRename({ id: 0, name: '创建中' })
  assert.equal(c.renameDialog.visible, false)
  c.openRename({ id: 7, name: '原名称' })
  for (const name of ['  ', '字'.repeat(81)]) {
    c.renameDialog.name = name
    await c.saveRename()
    assert.ok(c.renameDialog.error)
  }
  assert.deepEqual(c.calls, [])
})

test('请求失败保留名称并可重试；提交中不能取消或重复提交', async () => {
  let reject
  const c = controller(() => new Promise((_, fail) => { reject = fail }))
  c.openRename({ id: 8, name: '原名称' })
  c.renameDialog.name = '新名称'
  const saving = c.saveRename()
  c.closeRename()
  await c.saveRename()
  assert.equal(c.calls.length, 1)
  assert.equal(c.renameDialog.visible, true)
  reject(new Error('保存失败'))
  await saving
  assert.equal(c.renameDialog.saving, false)
  assert.equal(c.renameDialog.name, '新名称')
  assert.equal(c.renameDialog.error, '保存失败')
  c.closeRename()
  assert.equal(c.renameDialog.visible, false)
})
