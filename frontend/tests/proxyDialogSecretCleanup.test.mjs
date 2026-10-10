import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const sourceUrl = new URL('../src/views/Proxies.vue', import.meta.url)
const source = await readFile(sourceUrl, 'utf8')

function section(startMarker, endMarker) {
  const start = source.indexOf(startMarker)
  const end = source.indexOf(endMarker, start)
  assert.notEqual(start, -1, `未找到 ${startMarker}`)
  assert.notEqual(end, -1, `未找到 ${endMarker}`)
  return source.slice(start, end)
}

test('代理编辑弹窗的全部关闭入口都会清除凭据状态', () => {
  const resetBody = section('function resetProxyDialog()', 'function closeProxyDialog()')
  const closeBody = section('function closeProxyDialog()', 'function openEdit(')
  const beforeCloseBody = section(
    'function beforeProxyDialogClose(',
    'function beforeImportDialogClose(',
  )
  const saveBody = section('async function saveProxy()', 'async function testProxy(')

  assert.match(resetBody, /proxyDialog\.form = blankProxyForm\(\)/)
  assert.match(resetBody, /proxyDialog\.hasPassword = false/)
  assert.match(resetBody, /proxyDialog\.hasSecret = false/)
  assert.match(resetBody, /proxyDialog\.hasResinAdminToken = false/)
  assert.match(closeBody, /resetProxyDialog\(\)[\s\S]*proxyDialog\.visible = false/)
  assert.match(beforeCloseBody, /resetProxyDialog\(\)[\s\S]*done\(\)/)
  assert.match(saveBody, /proxyDialog\.visible = false\s+resetProxyDialog\(\)/)
  assert.match(source, /@closed="resetProxyDialog"/)
  assert.match(source, /@click="closeProxyDialog">取消/)
})

test('批量导入弹窗关闭和重新打开时都会销毁代理文本', () => {
  const openBody = section('function openImportDialog()', 'function resetImportDialog()')
  const resetBody = section('function resetImportDialog()', 'function closeImportDialog()')
  const closeBody = section('function closeImportDialog()', 'function onProxyKindChange(')
  const beforeCloseBody = section(
    'function beforeImportDialogClose(',
    'async function loadProxies(',
  )
  const importBody = section('async function importProxyText()', 'function openLightweightWarpCreate()')

  assert.match(resetBody, /importDialog\.text = ''/)
  assert.match(resetBody, /importDialog\.testAfterImport = false/)
  assert.match(openBody, /resetImportDialog\(\)[\s\S]*importDialog\.visible = true/)
  assert.match(closeBody, /resetImportDialog\(\)[\s\S]*importDialog\.visible = false/)
  assert.match(beforeCloseBody, /resetImportDialog\(\)[\s\S]*done\(\)/)
  assert.match(importBody, /importDialog\.visible = false\s+resetImportDialog\(\)/)
  assert.match(source, /@closed="resetImportDialog"/)
  assert.match(source, /@click="closeImportDialog">取消/)
})
