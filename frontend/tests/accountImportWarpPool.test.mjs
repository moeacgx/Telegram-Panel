import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const source = await readFile(new URL('../src/views/AccountImport.vue', import.meta.url), 'utf8')

test('账号导入允许自动分配空闲 WARP 和创建一对一 WARP', () => {
  assert.match(source, /value="warp_pool"[^>]*>自动分配空闲 WARP/)
  assert.match(source, /value="warp_per_account"[^>]*:disabled="!warpCreateAvailable"[^>]*>创建一对一 WARP/)
  assert.match(source, /不会创建新容器/)
  assert.match(source, /将为每个成功导入账号创建并绑定一个轻量 WARP/)
})

test('自动 WARP 池不依赖创建环境，创建模式依赖运行环境', () => {
  assert.doesNotMatch(source, /proxyStrategy\.value === 'warp_pool' && !warpAvailable\.value/)
  assert.match(source, /warpStatus\.value\?\.available/)
  assert.match(source, /panelApi\.wgcfStatus\(\)/)
  assert.match(source, /const selectedStrategy = proxyStrategy\.value/)
  assert.match(source, /form\.append\('proxyStrategy', strategy\)/)
})
