import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const proxiesSource = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
const panelApiSource = await readFile(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

test('WARP 页面明确区分读取状态、检测出口和重启恢复', () => {
  assert.match(proxiesSource, />刷新页面状态</)
  assert.match(proxiesSource, />\s*立即刷新旧版 WARP\s*</)
  assert.match(proxiesSource, /content="检测出口 IP（不重启）"/)
  assert.match(proxiesSource, /content="重启并恢复此 WARP"/)
})

test('WARP 自动维护状态展示巡检阈值和定时刷新语义', () => {
  assert.match(typesSource, /interface WarpMaintenanceRuntimeStatus/)
  assert.match(typesSource, /healthCheckIntervalMinutes: number/)
  assert.match(typesSource, /failureThreshold: number/)
  assert.match(typesSource, /scheduledRefreshEnabled: boolean/)
  assert.match(proxiesSource, /连续 \{\{ maintenance\.failureThreshold \}\} 次失败后自动恢复/)
  assert.match(proxiesSource, /健康时保持当前出口，不主动更换 IP/)
})

test('WARP 支持单个与批量手动恢复并自动更新页面状态', () => {
  assert.match(panelApiSource, /\/proxies\/\$\{id\}\/warp\/refresh/)
  assert.match(panelApiSource, /\/proxies\/warp\/refresh-all/)
  assert.match(proxiesSource, /const AUTO_STATUS_REFRESH_MS = 30_000/)
  assert.match(proxiesSource, /onBeforeUnmount/)
  assert.match(proxiesSource, /refreshingIds/)
  assert.match(proxiesSource, /refreshingAllWarps\.value \|\| refreshingIds\.size > 0/)
  assert.match(typesSource, /warpRuntimeStatus\?: string \| null/)
  assert.match(typesSource, /warpConsecutiveFailures\?: number/)
  assert.match(proxiesSource, /row\.warpRuntimeStatus/)
})
