import assert from 'node:assert/strict'
import { webcrypto } from 'node:crypto'
import { readFile } from 'node:fs/promises'
import test from 'node:test'
import ts from 'typescript'

const source = await readFile(new URL('../src/utils/requestId.ts', import.meta.url), 'utf8')
const js = ts.transpileModule(source, { compilerOptions: { module: ts.ModuleKind.ESNext } }).outputText
const { createRequestId } = await import(`data:text/javascript;base64,${Buffer.from(js).toString('base64')}`)

test('支持原生 UUID 时使用浏览器实现', () => {
  assert.equal(createRequestId({ randomUUID: () => 'native-id' }), 'native-id')
})

test('HTTP 回退生成 UUID v4 并保留随机位', () => {
  const provider = { getRandomValues(bytes) { bytes.fill(255); return bytes } }
  assert.equal(createRequestId(provider), 'ffffffff-ffff-4fff-bfff-ffffffffffff')
  const ids = Array.from({ length: 100 }, () => createRequestId({ getRandomValues: webcrypto.getRandomValues.bind(webcrypto) }))
  assert.equal(new Set(ids).size, 100)
  for (const id of ids) assert.match(id, /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
})

test('缺少安全随机源时明确拒绝，不回退到弱随机数', () => {
  assert.throws(() => createRequestId({}), /浏览器不支持安全随机数/)
})
