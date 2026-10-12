import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

const source = await readFile(new URL('../src/api/client.ts', import.meta.url), 'utf8')
function client() {
  let request
  let calls = 0
  const api = { get: async () => ({ data: { token: `身份令牌-${++calls}` } }),
    interceptors: { request: { use: fn => { request = fn } }, response: { use() {} } } }
  const stripped = source.replace(/^import .*\r?\n/gm, '').replace(/export /g, '')
  const js = ts.transpileModule(stripped, { compilerOptions: { target: ts.ScriptTarget.ES2020 } }).outputText
  new Function('axios', 'ElMessage', 'window', js)({ create: () => api }, {}, {})
  return { request: config => request(config), calls: () => calls }
}

test('登录与改密分别获取当前身份防伪令牌，不复用匿名令牌', async () => {
  const c = client()
  const sent = []
  for (const url of ['/auth/login', '/settings/password', '/settings/username']) {
    await c.request({ method: 'post', url, headers: { set: (key, value) => sent.push([key, value]) } })
  }
  assert.deepEqual(sent.map(x => x[1]), ['身份令牌-1', '身份令牌-2', '身份令牌-3'])
  assert.ok(sent.every(x => x[0] === 'X-TelegramPanel-CSRF'))
})

test('防伪令牌获取不递归，不修改普通业务请求', async () => {
  const c = client()
  for (const [method, url] of [['get', '/auth/csrf'], ['get', '/auth/me'], ['post', '/proxies']]) {
    const config = { method, url }
    assert.equal(await c.request(config), config)
  }
  assert.equal(c.calls(), 0)
})
