import type { OutboundProxy, WgcfProfile, WgcfRuntimeStatus } from '@/api/types'

export interface ProxyTableRow extends OutboundProxy {
  rowKey: string
  wgcfProfile?: WgcfProfile
}

/** 按档案标识合并运行器与数据库快照，创建中的出口也必须留在同一张表里。 */
export function mergeProxyRows(proxies: OutboundProxy[], profiles: WgcfProfile[]): ProxyTableRow[] {
  const rows: ProxyTableRow[] = proxies.map((proxy) => ({
    ...proxy,
    rowKey: proxy.managedWgcfProfile ? `wgcf:${proxy.managedWgcfProfile}` : `proxy:${proxy.id}`,
  }))
  const profileRows = new Map<string, number>()
  const proxyRows = new Map<number, number>()
  rows.forEach((row, index) => {
    if (row.managedWgcfProfile) profileRows.set(row.managedWgcfProfile, index)
    proxyRows.set(row.id, index)
  })

  for (const profile of profiles) {
    const index = profileRows.get(profile.profile) ?? (profile.proxyId ? proxyRows.get(profile.proxyId) : undefined)
    const proxy = index === undefined ? undefined : rows[index]
    const accountCount = Math.max(profile.accountCount, proxy?.accountCount ?? 0)
    const row: ProxyTableRow = {
      id: profile.proxyId ?? proxy?.id ?? 0,
      name: profile.name,
      kind: 'wireguard_warp',
      protocol: 'socks5',
      host: '',
      port: 0,
      createdAtUtc: '',
      updatedAtUtc: '',
      ...proxy,
      rowKey: `wgcf:${profile.profile}`,
      managedWgcfProfile: profile.profile,
      wgcfProfile: { ...profile, accountCount },
      isEnabled: profile.desired,
      testStatus: profile.testStatus,
      egressIp: profile.egressIp,
      warpStatus: profile.testStatus === 'ok' ? 'on' : null,
      lastError: profile.error,
      accountCount,
      usageCount: accountCount,
      isInUse: accountCount > 0 || !!proxy?.isGlobal,
    }
    if (index === undefined) {
      profileRows.set(profile.profile, rows.length)
      if (profile.proxyId) proxyRows.set(profile.proxyId, rows.length)
      rows.push(row)
    } else {
      profileRows.set(profile.profile, index)
      rows[index] = row
    }
  }
  return rows
}

export function wgcfPhaseLabel(profile: WgcfProfile) {
  return { creating: '创建中', ready: '已就绪', failed: '创建失败', stopped: '已停止', starting: '启动中' }[profile.phase] || '未知状态'
}

export function wgcfRuntimeLabel(runtime: string) {
  return ({ listening: '进程监听中', stopped: '进程已停止', starting: '进程启动中', failed: '进程异常', pending: '等待执行', unknown: '进程状态未知' } as Record<string, string>)[runtime] || '进程状态未知'
}

/** 页面只统计可独占领取的轻量出口，实际领取仍由后端原子租约复核。 */
export function countAvailableWgcfPool(proxies: OutboundProxy[], status: WgcfRuntimeStatus | null): number {
  if (!status?.available) return 0
  const readyProfiles = new Set(status.profiles.filter((profile) => profile.poolEligible === true && profile.phase === 'ready'
    && profile.desired && profile.runtime === 'listening' && profile.testStatus === 'ok'
    && !!profile.egressIp && profile.accountCount === 0).map((profile) => profile.profile))
  return proxies.filter((proxy) => !!proxy.managedWgcfProfile && readyProfiles.has(proxy.managedWgcfProfile)
    && proxy.isEnabled && proxy.testStatus === 'ok' && !!proxy.egressIp
    && (proxy.accountCount ?? 0) === 0 && (proxy.usageCount ?? 0) === 0 && !proxy.isGlobal).length
}
