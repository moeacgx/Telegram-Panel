import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const proxiesSource = await readFile(new URL('../src/views/Proxies.vue', import.meta.url), 'utf8')
const accountLoginSource = await readFile(new URL('../src/views/AccountLogin.vue', import.meta.url), 'utf8')
const accountImportSource = await readFile(new URL('../src/views/AccountImport.vue', import.meta.url), 'utf8')
const dashboardSource = await readFile(new URL('../src/views/Dashboard.vue', import.meta.url), 'utf8')
const typesSource = await readFile(new URL('../src/api/types.ts', import.meta.url), 'utf8')
const egressUtilSource = await readFile(new URL('../src/utils/networkEgress.ts', import.meta.url), 'utf8')

test('面板出口与独立 WARP 出口使用中文语义并明确区分', () => {
  assert.doesNotMatch(proxiesSource, /WARP \{\{ panelEgress\.warpStatus \}\}/)
  assert.doesNotMatch(dashboardSource, /WARP \{\{ egress\.warpStatus \}\}/)
  assert.match(egressUtilSource, /if \(normalized === 'off'\) return '未使用 WARP'/)
  assert.match(proxiesSource, /不代表下方独立 WARP 代理失效/)
  assert.match(dashboardSource, /不代表代理管理中的独立 WARP 失效/)
  assert.match(proxiesSource, /warpStatusLabel\(row\.warpStatus\)/)
})

test('出口 IP 明确显示协议栈与检测状态', () => {
  assert.match(proxiesSource, /label="公网出口 IP"/)
  assert.match(proxiesSource, /ipVersionLabel\(proxy\.egressIp\)/)
  assert.match(proxiesSource, /'检测失败'[\s\S]*'尚未检测'/)
  assert.match(egressUtilSource, /normalized\.includes\(':'\) \? 'IPv6' : 'IPv4'/)
})

test('一键创建 WARP 支持默认协议和单次 HTTP SOCKS5 覆盖', () => {
  assert.match(typesSource, /defaultProtocol: WarpProxyProtocol/)
  assert.match(typesSource, /protocol\?: WarpProxyProtocol \| null/)
  assert.match(proxiesSource, /v-model="warpDialog\.protocol"/)
  assert.match(proxiesSource, /value="http">HTTP/)
  assert.match(proxiesSource, /value="socks5">SOCKS5/)
  assert.match(proxiesSource, /warpStatus\.value\?\.defaultProtocol === 'socks5'/)
  assert.match(proxiesSource, /protocol: warpDialog\.protocol/)
})

test('WARP 创建与首次连接分配说明资源边界', () => {
  assert.match(proxiesSource, /每创建一个 WARP，都会启动一个独立 Docker 容器/)
  assert.match(proxiesSource, /持续占用服务器内存与少量 CPU/)
  assert.match(proxiesSource, /根据服务器资源控制创建数量/)
  assert.match(accountLoginSource, /不会创建新容器/)
  assert.doesNotMatch(accountImportSource, /每个账号都会创建一个独立 Docker 容器和数据卷/)
  assert.match(accountImportSource, /不会创建新的 Docker 容器|不会创建新容器/)
})
