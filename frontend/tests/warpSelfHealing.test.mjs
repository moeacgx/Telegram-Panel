import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const proxiesSource = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
const panelApiSource = await readFile(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')

test('统一代理表提供运行状态、出口检测与失败恢复操作', () => {
  assert.match(proxiesSource, />刷新页面状态</)
  for (const label of ['继续创建此出口', '启动出口', '停止出口', '检测 WARP 出口', '绑定单个账号']) {
    assert.ok(proxiesSource.includes(`aria-label="${label}"`))
  }
  assert.match(proxiesSource, /wgcfPhaseLabel\(row\.wgcfProfile\)/)
  assert.match(proxiesSource, /wgcfRuntimeLabel\(row\.wgcfProfile\.runtime\)/)
})

test('旧容器的运行环境、自动巡检、刷新 API 和客户端合同全部退出', () => {
  assert.doesNotMatch(proxiesSource, /旧版容器 WARP|立即刷新旧版 WARP|自动维护状态|loadWarpStatus|refreshAllWarps|refreshWarp\(/)
  assert.doesNotMatch(panelApiSource, /proxies\/warp\/status|warp\/refresh|refreshWarpProxy|refreshAllWarpProxies/)
  assert.doesNotMatch(typesSource, /interface WarpMaintenance|interface WarpRuntimeStatus/)
  assert.match(proxiesSource, /旧出口待迁移/)
})

test('页面刷新会同步轻量运行器，离开页面时停止轮询', () => {
  assert.match(proxiesSource, /wgcfWarpPanel\.value\?\.loadStatus\(\)/)
  assert.match(proxiesSource, /const AUTO_STATUS_REFRESH_MS = 30_000/)
  assert.match(proxiesSource, /onBeforeUnmount/)
  assert.match(proxiesSource, /clearInterval\(autoStatusRefreshTimer\)/)
})
