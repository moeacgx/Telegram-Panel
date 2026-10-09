<template>
  <div class="proxies-page">
    <section class="network-strip" aria-label="网络出口状态">
      <div class="network-main">
        <span class="material-icons network-icon">public</span>
        <div>
          <div class="network-label">面板公网出口</div>
          <div class="network-value">
            <span>{{ panelEgressText }}</span>
            <el-tooltip
              v-if="!egressLoading && !panelEgressError && panelEgress?.warpStatus"
              :content="panelWarpStatusHelp"
              placement="top"
            >
              <el-tag
                size="small"
                :type="isWarpConnected(panelEgress.warpStatus) ? 'success' : 'info'"
              >
                {{ warpStatusLabel(panelEgress.warpStatus) }}
              </el-tag>
            </el-tooltip>
          </div>
          <div class="network-meta">
            {{ panelEgressMeta }}
            <span v-if="!egressLoading && !panelEgressError && panelEgress?.latencyMs != null"> · {{ panelEgress.latencyMs }} ms</span>
          </div>
          <div class="network-scope-note">这里只检测面板服务自身；各代理的独立出口见下方列表。</div>
        </div>
      </div>
      <el-button
        circle
        :icon="Refresh"
        :loading="egressLoading"
        title="重新检测面板公网出口"
        @click="loadPanelEgress"
      />
    </section>

    <el-card shadow="never" class="page-card">
      <div class="toolbar">
        <el-button type="primary" :icon="CirclePlus" @click="openCreate">新增代理</el-button>
        <el-button :icon="Upload" @click="openImportDialog">批量导入</el-button>
        <el-tooltip :content="globalProxyHelp" placement="top">
          <el-button :icon="Setting" :loading="globalProxyLoading" @click="openGlobalProxyDialog">
            全局代理 · {{ globalProxyLabel }}
          </el-button>
        </el-tooltip>
        <el-button
          type="success"
          :icon="MagicStick"
          :disabled="warpUnavailable || refreshingAllWarps"
          @click="openWarpCreate()"
        >
          一键创建 WARP
        </el-button>
        <el-button
          type="warning"
          :icon="RefreshRight"
          :loading="refreshingAllWarps"
          :disabled="warpUnavailable || !hasEnabledWarp || refreshingAllWarps || refreshingIds.size > 0"
          @click="refreshAllWarps"
        >
          立即刷新全部 WARP
        </el-button>
        <el-button :icon="Refresh" :loading="loading" @click="refreshAll">刷新页面状态</el-button>
        <span class="toolbar-spacer" />
        <div class="warp-runtime">
          <span>WARP 运行环境</span>
          <el-tag :type="warpStatusType" size="small">{{ warpStatusText }}</el-tag>
        </div>
      </div>
      <div v-if="globalProxyError" class="runtime-error">全局代理：{{ globalProxyError }}</div>
      <div v-if="warpStatusError || warpStatus?.error" class="runtime-error">{{ warpStatusError || warpStatus?.error }}</div>
      <div v-if="maintenance && warpStatus?.enabled" class="warp-maintenance" aria-label="WARP 自动维护状态">
        <div class="maintenance-state">
          <el-tag :type="maintenanceStatusType" size="small">
            {{ maintenanceStatusText }}
          </el-tag>
          <span>每 {{ maintenance.healthCheckIntervalMinutes }} 分钟巡检</span>
          <span>连续 {{ maintenance.failureThreshold }} 次失败后自动恢复</span>
          <span>恢复冷却 {{ maintenance.recoveryCooldownMinutes }} 分钟</span>
        </div>
        <div class="maintenance-schedule">
          <span v-if="maintenance.scheduledRefreshEnabled">
            定时刷新：每 {{ formatDuration(maintenance.scheduledRefreshIntervalMinutes) }}
          </span>
          <span v-else>健康时保持当前出口，不主动更换 IP</span>
          <span v-if="maintenance.lastRunAtUtc">最近巡检：{{ formatTime(maintenance.lastRunAtUtc, '-') }}</span>
          <span v-if="maintenance.nextRunAtUtc">下次巡检：{{ formatTime(maintenance.nextRunAtUtc, '-') }}</span>
          <span v-if="maintenance.lastRunAtUtc">
            上次结果：{{ maintenance.checkedCount }} 个，正常 {{ maintenance.healthyCount }}，
            已恢复 {{ maintenance.recoveredCount }}，异常 {{ maintenance.failedCount }}
          </span>
        </div>
        <div v-if="maintenance.lastError" class="maintenance-error">
          最近错误：{{ maintenance.lastError }}
        </div>
      </div>
    </el-card>

    <WgcfWarpPanel @changed="loadProxies(false)" />

    <el-dialog
      v-model="globalProxyDialog.visible"
      title="账号全局代理设置"
      width="min(560px, calc(100vw - 24px))"
      :before-close="beforeGlobalProxyDialogClose"
      :close-on-click-modal="!globalProxyDialog.saving"
      :close-on-press-escape="!globalProxyDialog.saving"
      :show-close="!globalProxyDialog.saving"
      @closed="resetGlobalProxyDialog"
    >
      <el-alert
        class="mb-3"
        type="info"
        :closable="false"
        show-icon
        title="未绑定账号专属代理的账号默认使用这里的全局出口"
        description="账号明确选择直连时除外；账号专属代理优先。保存后会清理现有 Telegram 客户端连接，下次连接从一开始使用新代理，不会先直连再切换。"
      />
      <el-form :model="globalProxyDialog.form" label-position="top" :disabled="globalProxyDialog.saving">
        <el-switch v-model="globalProxyDialog.form.enabled" active-text="启用账号全局代理" />
        <template v-if="globalProxyDialog.form.enabled">
          <el-form-item label="配置方式" class="mt-3" required>
            <el-radio-group v-model="globalProxyDialog.form.sourceMode">
              <el-radio-button value="existing">从已有代理选择</el-radio-button>
              <el-radio-button value="manual">手动配置</el-radio-button>
            </el-radio-group>
          </el-form-item>

          <template v-if="globalProxyDialog.form.sourceMode === 'existing'">
            <el-form-item label="已有代理" required>
              <el-select
                v-model="globalProxyDialog.form.proxyId"
                class="full"
                filterable
                placeholder="选择普通代理、外部 WireGuard WARP、Resin 或受管 WARP"
              >
                <el-option-group
                  v-for="group in globalProxyGroups"
                  :key="group.kind"
                  :label="group.label"
                >
                  <el-option
                    v-for="proxy in group.items"
                    :key="proxy.id"
                    :label="`${proxy.name} · ${protocolLabel(proxy.protocol)} · ${proxy.host}:${proxy.port}`"
                    :value="proxy.id"
                    :disabled="!proxy.isEnabled"
                  />
                </el-option-group>
              </el-select>
            </el-form-item>
            <div v-if="globalSelectedProxy" class="selected-proxy-summary">
              <div class="selected-proxy-heading">
                <el-tag :type="kindTagType(globalSelectedProxy.kind)" size="small">
                  {{ kindLabel(globalSelectedProxy.kind) }}
                </el-tag>
                <strong>{{ globalSelectedProxy.name }}</strong>
                <el-tag v-if="globalSelectedProxy.category" size="small" effect="plain">
                  {{ globalSelectedProxy.category.name }}
                </el-tag>
              </div>
              <div class="cell-sub monospace">{{ globalSelectedProxy.host }}:{{ globalSelectedProxy.port }}</div>
              <div class="cell-sub">
                出口 IP：{{ egressIpText(globalSelectedProxy) }}
                <span v-if="globalSelectedProxy.egressCountry || globalSelectedProxy.egressCity">
                  · {{ [globalSelectedProxy.egressCountry, globalSelectedProxy.egressCity].filter(Boolean).join(' / ') }}
                </span>
              </div>
            </div>
            <el-alert
              v-else-if="proxies.length === 0"
              type="warning"
              :closable="false"
              title="还没有可选代理，请先创建 WARP、导入外部 WireGuard WARP、Resin 或普通代理"
            />
            <div class="global-proxy-actions">
              <el-button
                type="success"
                plain
                :icon="MagicStick"
                :disabled="warpUnavailable"
                @click="openWarpCreateForGlobal()"
              >
                一键创建 WARP
              </el-button>
              <el-button plain :icon="CirclePlus" @click="openProxyCreateForGlobal('resin')">
                新增 Resin
              </el-button>
              <el-button plain :icon="CirclePlus" @click="openProxyCreateForGlobal('wireguard_warp')">
                新增外部 WireGuard WARP
              </el-button>
              <el-button plain :icon="CirclePlus" @click="openProxyCreateForGlobal('manual')">
                新增普通代理
              </el-button>
            </div>
          </template>

          <template v-else>
            <el-form-item label="代理协议" required>
              <el-radio-group v-model="globalProxyDialog.form.protocol">
                <el-radio-button value="http">HTTP</el-radio-button>
                <el-radio-button value="socks5">SOCKS5</el-radio-button>
                <el-radio-button value="mtproto">MTProxy</el-radio-button>
              </el-radio-group>
            </el-form-item>
            <div class="form-grid">
              <el-form-item label="代理主机" required>
                <el-input v-model="globalProxyDialog.form.server" maxlength="253" placeholder="proxy.example.com" />
              </el-form-item>
              <el-form-item label="端口" required>
                <el-input-number
                  v-model="globalProxyDialog.form.port"
                  :min="1"
                  :max="65535"
                  controls-position="right"
                  class="full"
                />
              </el-form-item>
              <el-form-item v-if="globalProxyDialog.form.protocol !== 'mtproto'" label="用户名">
                <el-input
                  v-model="globalProxyDialog.form.username"
                  autocomplete="off"
                  maxlength="255"
                  placeholder="留空表示不使用用户名"
                />
              </el-form-item>
              <el-form-item v-if="globalProxyDialog.form.protocol !== 'mtproto'" label="密码">
                <div class="credential-field">
                  <el-input
                    v-model="globalProxyDialog.form.password"
                    type="password"
                    show-password
                    autocomplete="new-password"
                    maxlength="255"
                    :disabled="globalProxyDialog.form.clearPassword"
                    :placeholder="globalProxyDialog.hasPassword
                      ? '留空保持原密码'
                      : '留空表示不使用密码'"
                  />
                  <el-checkbox
                    v-if="globalProxyDialog.hasPassword"
                    v-model="globalProxyDialog.form.clearPassword"
                  >
                    清除已保存的密码
                  </el-checkbox>
                </div>
              </el-form-item>
            </div>
            <el-form-item v-if="globalProxyDialog.form.protocol === 'mtproto'" label="MTProxy Secret" required>
              <el-input
                v-model="globalProxyDialog.form.secret"
                type="password"
                show-password
                autocomplete="new-password"
                maxlength="500"
                :placeholder="globalProxyDialog.hasSecret ? '留空保持原 Secret' : '请输入 MTProxy Secret'"
              />
            </el-form-item>
          </template>
        </template>
        <el-alert
          v-else
          class="mt-3"
          type="warning"
          :closable="false"
          show-icon
          title="停用后，选择“全局代理”的账号将无法连接"
          description="系统会保持失败闭锁，不会静默回退到面板直连。请先给这些账号切换代理或明确选择直连。"
        />
      </el-form>
      <template #footer>
        <el-button :disabled="globalProxyDialog.saving" @click="closeGlobalProxyDialog">取消</el-button>
        <el-button type="primary" :loading="globalProxyDialog.saving" @click="saveGlobalProxy">保存</el-button>
      </template>
    </el-dialog>

    <el-card shadow="never" class="page-card mt-4">
      <div class="proxy-list-header">
        <el-tabs v-model="usageFilter" class="usage-tabs">
          <el-tab-pane name="all">
            <template #label>
              <span class="usage-tab-label">全部 <strong>{{ usageCounts.all }}</strong></span>
            </template>
          </el-tab-pane>
          <el-tab-pane name="used">
            <template #label>
              <span class="usage-tab-label">使用中 <strong>{{ usageCounts.used }}</strong></span>
            </template>
          </el-tab-pane>
          <el-tab-pane name="unused">
            <template #label>
              <span class="usage-tab-label">未使用 <strong>{{ usageCounts.unused }}</strong></span>
            </template>
          </el-tab-pane>
        </el-tabs>
        <div class="proxy-list-actions">
          <el-select
            v-model="categoryFilter"
            class="category-filter"
            :loading="proxyCategoryLoading"
            placeholder="代理分类"
          >
            <el-option label="全部分类" value="all" />
            <el-option label="未分类" value="uncategorized" />
            <el-option
              v-for="category in proxyCategories"
              :key="category.id"
              :label="category.name"
              :value="category.id"
            />
          </el-select>
          <el-button
            :icon="CollectionTag"
            :disabled="selectedProxies.length === 0"
            @click="openBatchCategory"
          >
            设置分类<span v-if="selectedProxies.length">（{{ selectedProxies.length }}）</span>
          </el-button>
          <el-button
            type="danger"
            plain
            :icon="Delete"
            :loading="batchDeleting"
            :disabled="selectedProxies.length === 0 || batchDeleting || loading"
            @click="removeSelectedProxies"
          >
            批量删除<span v-if="selectedProxies.length">（{{ selectedProxies.length }}）</span>
          </el-button>
          <el-button :icon="FolderOpened" @click="openCategoryManager">管理分类</el-button>
        </div>
      </div>

      <el-table
        ref="proxyTableRef"
        v-loading="loading"
        :data="filteredProxies"
        stripe
        row-key="id"
        class="proxy-table"
        @selection-change="onProxySelectionChange"
      >
        <el-table-column type="selection" width="46" :reserve-selection="true" :selectable="isSelectableProxy" />
        <el-table-column label="名称" min-width="150">
          <template #default="{ row }">
            <div class="cell-main">{{ row.name }}</div>
            <div class="cell-sub">#{{ row.id }}</div>
          </template>
        </el-table-column>
        <el-table-column label="类型" width="100">
          <template #default="{ row }">
            <el-tag :type="kindTagType(row.kind)" size="small">{{ kindLabel(row.kind) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="协议" width="100">
          <template #default="{ row }">{{ protocolLabel(row.protocol) }}</template>
        </el-table-column>
        <el-table-column label="分类" min-width="110">
          <template #default="{ row }">
            <el-tag
              v-if="row.category"
              size="small"
              effect="plain"
              :style="categoryTagStyle(row.category)"
            >
              {{ row.category.name }}
            </el-tag>
            <span v-else class="cell-sub">未分类</span>
          </template>
        </el-table-column>
        <el-table-column label="代理地址" min-width="190">
          <template #default="{ row }">
            <div class="monospace">{{ row.host }}:{{ row.port }}</div>
            <div v-if="row.username" class="cell-sub">用户：{{ row.username }}</div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="92">
          <template #default="{ row }">
            <template v-if="row.kind === 'warp'">
              <el-tooltip :content="warpRuntimeHelp(row)" placement="top">
                <el-tag :type="warpRuntimeStatusType(row.warpRuntimeStatus)" size="small">
                  {{ warpRuntimeStatusLabel(row.warpRuntimeStatus) }}
                </el-tag>
              </el-tooltip>
              <div class="cell-sub">期望{{ row.isEnabled ? '启用' : '停用' }}</div>
            </template>
            <el-tag v-else :type="row.isEnabled ? 'success' : 'info'" size="small">
              {{ row.isEnabled ? '已启用' : '已停用' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="公网出口 IP" min-width="260">
          <template #default="{ row }">
            <div class="egress-detail">
              <span class="egress-field-label">IP</span>
              <span class="monospace">{{ egressIpText(row) }}</span>
              <el-tag v-if="row.egressIp" size="small" effect="plain">
                {{ egressIpVersion(row) }}
              </el-tag>
            </div>
            <div v-if="row.egressIp" class="egress-detail cell-sub">
              <span class="egress-field-label">地区</span>
              <span>{{ [row.egressCountry, row.egressCity].filter(Boolean).join(' / ') || '未知' }}</span>
            </div>
            <div v-if="row.egressIp" class="egress-detail cell-sub">
              <span class="egress-field-label">ISP</span>
              <span>{{ row.egressIsp || '未知' }}</span>
            </div>
            <el-tag
              v-if="row.kind === 'warp'"
              class="warp-egress-tag"
              :type="warpProxyStatusType(row.warpStatus)"
              size="small"
              effect="plain"
            >
              {{ warpStatusLabel(row.warpStatus) }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="检测" min-width="170">
          <template #default="{ row }">
            <el-tooltip :content="row.lastError || testStatusLabel(row.testStatus)" placement="top">
              <el-tag :type="testStatusType(row.testStatus)" size="small">
                {{ testStatusLabel(row.testStatus) }}
                <span v-if="row.lastLatencyMs != null"> · {{ row.lastLatencyMs }} ms</span>
              </el-tag>
            </el-tooltip>
            <div v-if="row.lastTestedAtUtc" class="cell-sub">{{ formatTime(row.lastTestedAtUtc, '-') }}</div>
            <div v-if="row.kind === 'warp' && (row.warpConsecutiveFailures ?? 0) > 0" class="cell-sub failure-count">
              连续失败 {{ row.warpConsecutiveFailures }} 次
            </div>
            <div v-if="row.kind === 'warp' && row.warpLastRecoveredAtUtc" class="cell-sub">
              最近恢复 {{ formatTime(row.warpLastRecoveredAtUtc, '-') }}
            </div>
          </template>
        </el-table-column>
        <el-table-column label="使用情况" width="110" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.isGlobal" type="warning" size="small">全局</el-tag>
            <el-tag :type="proxyIsUsed(row) ? 'success' : 'info'" size="small">
              {{ proxyIsUsed(row) ? '使用中' : '未使用' }}
            </el-tag>
            <div class="cell-sub">{{ proxyUsageCount(row) }} 个账号</div>
          </template>
        </el-table-column>
        <el-table-column label="操作" width="158" fixed="right">
          <template #default="{ row }">
            <div v-if="!row.managedWgcfProfile" class="row-actions">
              <el-tooltip content="检测出口 IP（不重启）" placement="top">
                <el-button
                  link
                  type="primary"
                  :icon="VideoPlay"
                  :loading="testingIds.has(row.id)"
                  :disabled="refreshingAllWarps || testingIds.has(row.id) || refreshingIds.has(row.id) || deletingIds.has(row.id) || !row.isEnabled"
                  title="检测出口 IP"
                  @click="testProxy(row)"
                />
              </el-tooltip>
              <el-tooltip v-if="row.kind === 'warp'" content="重启并恢复此 WARP" placement="top">
                <el-button
                  link
                  type="warning"
                  :icon="RefreshRight"
                  :loading="refreshingIds.has(row.id)"
                  :disabled="warpUnavailable || refreshingAllWarps || refreshingIds.has(row.id) || testingIds.has(row.id) || deletingIds.has(row.id) || !row.isEnabled"
                  title="重启并恢复此 WARP"
                  @click="refreshWarp(row)"
                />
              </el-tooltip>
              <el-tooltip v-if="row.kind !== 'warp'" content="编辑代理" placement="top">
                <el-button
                  link
                  type="primary"
                  :icon="Edit"
                  :disabled="deletingIds.has(row.id)"
                  title="编辑代理"
                  @click="openEdit(row)"
                />
              </el-tooltip>
              <el-tooltip content="删除代理" placement="top">
                <el-button
                  link
                  type="danger"
                  :icon="Delete"
                  :loading="deletingIds.has(row.id)"
                  :disabled="refreshingAllWarps || deletingIds.has(row.id) || testingIds.has(row.id) || refreshingIds.has(row.id)"
                  title="删除代理"
                  @click="removeProxy(row)"
                />
              </el-tooltip>
            </div>
            <el-tag v-else size="small" effect="plain">轻量 WARP 受管</el-tag>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty :description="proxies.length ? '当前筛选下没有代理' : '暂无代理'" />
        </template>
      </el-table>
    </el-card>

    <el-dialog
      v-model="batchCategoryDialog.visible"
      title="批量设置代理分类"
      width="min(440px, calc(100vw - 24px))"
      :close-on-click-modal="!batchCategoryDialog.saving"
      :close-on-press-escape="!batchCategoryDialog.saving"
      :show-close="!batchCategoryDialog.saving"
    >
      <el-form label-position="top" :disabled="batchCategoryDialog.saving">
        <el-form-item :label="`已选择 ${batchCategoryDialog.proxyIds.length} 个代理`">
          <el-select v-model="batchCategoryDialog.categoryId" class="full" clearable placeholder="未分类">
            <el-option
              v-for="category in proxyCategories"
              :key="category.id"
              :label="category.name"
              :value="category.id"
            />
          </el-select>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="batchCategoryDialog.saving" @click="batchCategoryDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="batchCategoryDialog.saving" @click="saveBatchCategory">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="categoryManagerDialog.visible"
      title="代理分类管理"
      width="min(620px, calc(100vw - 24px))"
    >
      <div class="category-manager-toolbar">
        <el-button type="primary" :icon="CirclePlus" @click="openCategoryEditor()">新增分类</el-button>
      </div>
      <el-table v-loading="categoryManagerDialog.loading" :data="proxyCategories" row-key="id" max-height="420">
        <el-table-column label="分类" min-width="160">
          <template #default="{ row }">
            <el-tag size="small" effect="plain" :style="categoryTagStyle(row)">{{ row.name }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="description" label="说明" min-width="200">
          <template #default="{ row }">{{ row.description || '-' }}</template>
        </el-table-column>
        <el-table-column label="代理数" width="90" align="center">
          <template #default="{ row }">{{ categoryProxyCount(row) }}</template>
        </el-table-column>
        <el-table-column label="操作" width="100" align="right">
          <template #default="{ row }">
            <el-button link type="primary" :icon="Edit" title="编辑分类" @click="openCategoryEditor(row)" />
            <el-button link type="danger" :icon="Delete" title="删除分类" @click="removeCategory(row)" />
          </template>
        </el-table-column>
        <template #empty>
          <el-empty description="暂无分类" />
        </template>
      </el-table>
    </el-dialog>

    <el-dialog
      v-model="categoryEditorDialog.visible"
      :title="categoryEditorDialog.id ? '编辑代理分类' : '新增代理分类'"
      width="min(440px, calc(100vw - 24px))"
      append-to-body
      :close-on-click-modal="!categoryEditorDialog.saving"
      :close-on-press-escape="!categoryEditorDialog.saving"
      :show-close="!categoryEditorDialog.saving"
      @closed="resetCategoryEditor"
    >
      <el-form :model="categoryEditorDialog.form" label-position="top" :disabled="categoryEditorDialog.saving">
        <el-form-item label="分类名称" required>
          <el-input v-model="categoryEditorDialog.form.name" maxlength="40" placeholder="例如：美国住宅" />
        </el-form-item>
        <el-form-item label="标签颜色">
          <el-color-picker v-model="categoryEditorDialog.form.color" />
        </el-form-item>
        <el-form-item label="说明">
          <el-input
            v-model="categoryEditorDialog.form.description"
            type="textarea"
            :rows="3"
            maxlength="200"
            show-word-limit
          />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="categoryEditorDialog.saving" @click="categoryEditorDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="categoryEditorDialog.saving" @click="saveCategory">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="proxyDialog.visible"
      class="proxy-editor-dialog"
      :title="proxyDialog.id ? '编辑代理' : '新增代理'"
      width="min(620px, calc(100vw - 24px))"
      append-to-body
      :before-close="beforeProxyDialogClose"
      :close-on-click-modal="!proxyDialog.saving"
      :close-on-press-escape="!proxyDialog.saving"
      :show-close="!proxyDialog.saving"
      @closed="resetProxyDialog"
    >
      <el-form :model="proxyDialog.form" label-position="top" :disabled="proxyDialog.saving">
        <el-form-item label="代理类型" prop="kind" required>
          <el-radio-group v-model="proxyDialog.form.kind" @change="onProxyKindChange">
            <el-radio-button value="manual">普通代理</el-radio-button>
            <el-radio-button value="wireguard_warp">外部 WireGuard WARP</el-radio-button>
            <el-radio-button value="resin">Resin 动态代理</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-alert
          v-if="proxyDialog.form.kind === 'wireguard_warp'"
          class="mb-3"
          type="info"
          :closable="false"
          show-icon
          title="面板只管理 HTTP/SOCKS 监听，不管理宿主 WireGuard"
          description="请先在面板外运行 wg/gost 等服务，并填写它暴露给面板容器访问的 HTTP 或 SOCKS5 地址；保存后检测会要求 Cloudflare Trace 返回 WARP 已启用。"
        />
        <div class="form-grid">
          <el-form-item label="名称" prop="name" required>
            <el-input v-model="proxyDialog.form.name" maxlength="80" placeholder="例如：香港 Socks5" />
          </el-form-item>
          <el-form-item label="分类">
            <el-select v-model="proxyDialog.form.categoryId" class="full" clearable filterable placeholder="未分类">
              <el-option
                v-for="category in proxyCategories"
                :key="category.id"
                :label="category.name"
                :value="category.id"
              />
            </el-select>
          </el-form-item>
          <el-form-item label="协议" prop="protocol" required>
            <el-select v-model="proxyDialog.form.protocol" class="full">
              <el-option label="SOCKS5" value="socks5" />
              <el-option label="HTTP" value="http" />
              <el-option v-if="proxyDialog.form.kind === 'manual'" label="MTProto" value="mtproto" />
            </el-select>
          </el-form-item>
          <el-form-item label="主机" prop="host" required>
            <el-input v-model="proxyDialog.form.host" placeholder="proxy.example.com" />
          </el-form-item>
          <el-form-item label="端口" prop="port" required>
            <el-input-number v-model="proxyDialog.form.port" :min="1" :max="65535" controls-position="right" class="full" />
          </el-form-item>
          <el-form-item :label="proxyDialog.form.kind === 'resin' ? '用户名（自动生成）' : '用户名'">
            <el-input
              v-model="proxyDialog.form.username"
              autocomplete="off"
              :disabled="proxyDialog.form.kind === 'resin'"
              :placeholder="proxyDialog.form.kind === 'resin'
                ? '由 Platform 和账号 ID 自动生成'
                : '留空表示不使用用户名'"
            />
          </el-form-item>
          <el-form-item :label="proxyDialog.form.kind === 'resin' ? 'Proxy Token' : '密码'">
            <div class="credential-field">
              <el-input
                v-model="proxyDialog.form.password"
                type="password"
                show-password
                autocomplete="new-password"
                :disabled="proxyDialog.form.clearPassword"
                :placeholder="proxyDialog.hasPassword
                  ? (proxyDialog.form.kind === 'resin' ? '留空保持原 Token' : '留空保持原密码')
                  : (proxyDialog.form.kind === 'resin' ? '可留空，需与 Resin 配置一致' : '')"
              />
              <el-checkbox
                v-if="proxyDialog.id && proxyDialog.hasPassword"
                v-model="proxyDialog.form.clearPassword"
              >
                清除已保存的{{ proxyDialog.form.kind === 'resin' ? ' Proxy Token' : '密码' }}
              </el-checkbox>
            </div>
          </el-form-item>
        </div>
        <el-form-item v-if="proxyDialog.form.protocol === 'mtproto'" label="MTProto Secret" required>
          <el-input
            v-model="proxyDialog.form.secret"
            type="password"
            show-password
            :placeholder="proxyDialog.hasSecret ? '留空保持原 Secret' : ''"
          />
        </el-form-item>
        <template v-if="proxyDialog.form.kind === 'resin'">
          <el-divider content-position="left">Resin 配置</el-divider>
          <div class="resin-project-reference">
            <span>开源项目：</span>
            <el-link
              type="primary"
              href="https://github.com/Resinat/Resin"
              target="_blank"
              rel="noopener noreferrer"
            >
              Resinat/Resin
            </el-link>
          </div>
          <el-alert
            title="需要 Resin RESIN_AUTH_VERSION=V1；账号身份由面板自动保持稳定"
            type="info"
            :closable="false"
            show-icon
            class="mb-3"
          />
          <div class="form-grid">
            <el-form-item label="平台标识" required>
              <el-input v-model="proxyDialog.form.resinPlatform" placeholder="telegram-panel" />
            </el-form-item>
            <el-form-item label="管理地址">
              <el-input v-model="proxyDialog.form.resinAdminUrl" placeholder="留空表示不配置管理地址" />
            </el-form-item>
          </div>
          <el-form-item label="管理令牌">
            <div class="credential-field">
              <el-input
                v-model="proxyDialog.form.resinAdminToken"
                type="password"
                show-password
                autocomplete="new-password"
                :disabled="proxyDialog.form.clearResinAdminToken"
                :placeholder="proxyDialog.hasResinAdminToken ? '留空保持原令牌' : ''"
              />
              <el-checkbox
                v-if="proxyDialog.id && proxyDialog.hasResinAdminToken"
                v-model="proxyDialog.form.clearResinAdminToken"
              >
                清除已保存的管理令牌
              </el-checkbox>
            </div>
          </el-form-item>
        </template>
        <div class="form-options">
          <el-switch v-model="proxyDialog.form.isEnabled" active-text="启用代理" />
          <el-checkbox v-model="proxyDialog.form.testAfterSave">保存后检测出口</el-checkbox>
        </div>
      </el-form>
      <template #footer>
        <el-button :disabled="proxyDialog.saving" @click="closeProxyDialog">取消</el-button>
        <el-button type="primary" :loading="proxyDialog.saving" @click="saveProxy">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="importDialog.visible"
      title="批量导入代理"
      width="min(620px, calc(100vw - 24px))"
      :before-close="beforeImportDialogClose"
      :close-on-click-modal="!importDialog.importing"
      :close-on-press-escape="!importDialog.importing"
      :show-close="!importDialog.importing"
      @closed="resetImportDialog"
    >
      <el-form label-position="top" :disabled="importDialog.importing">
        <el-form-item label="代理文本">
          <el-input
            v-model="importDialog.text"
            type="textarea"
            :rows="10"
            placeholder="每行一个代理，例如 socks5://user:password@host:port；外部 WireGuard WARP 可用 wg-warp+socks5://127.0.0.1:1080 或 wg-warp+http://127.0.0.1:8080"
          />
        </el-form-item>
        <el-checkbox v-model="importDialog.testAfterImport">导入后逐个检测出口</el-checkbox>
      </el-form>
      <template #footer>
        <el-button :disabled="importDialog.importing" @click="closeImportDialog">取消</el-button>
        <el-button type="primary" :loading="importDialog.importing" @click="importProxyText">导入</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="warpDialog.visible"
      title="一键创建 WARP"
      width="min(500px, calc(100vw - 24px))"
      append-to-body
      :before-close="beforeWarpDialogClose"
      :close-on-click-modal="!warpDialog.creating"
      :close-on-press-escape="!warpDialog.creating"
      :show-close="!warpDialog.creating"
    >
      <el-form label-position="top" :disabled="warpDialog.creating">
        <el-alert
          class="mb-3"
          type="warning"
          :closable="false"
          show-icon
          title="每创建一个 WARP，都会启动一个独立 Docker 容器"
          description="每个容器还会保留独立数据卷，并持续占用服务器内存与少量 CPU；请根据服务器资源控制创建数量。"
        />
        <el-form-item label="代理名称">
          <el-input v-model="warpDialog.name" maxlength="80" placeholder="留空自动命名" />
        </el-form-item>
        <el-form-item label="代理协议">
          <el-radio-group v-model="warpDialog.protocol">
            <el-radio-button value="http">HTTP</el-radio-button>
            <el-radio-button value="socks5">SOCKS5</el-radio-button>
          </el-radio-group>
          <div class="form-hint">
            系统默认：{{ protocolLabel(warpStatus?.defaultProtocol || 'http') }}；本选项只覆盖这一次创建。
            WARP 容器同一端口同时支持 HTTP 和 SOCKS5。
          </div>
        </el-form-item>
        <el-alert
          v-if="warpStatus"
          :title="`镜像：${warpStatus.image}`"
          :description="`网络：${warpStatus.network} · 连接方式：${warpStatus.proxyHostMode} · 默认协议：${protocolLabel(warpStatus.defaultProtocol)}`"
          type="info"
          :closable="false"
          show-icon
        />
      </el-form>
      <template #footer>
        <el-button :disabled="warpDialog.creating" @click="closeWarpDialog">取消</el-button>
        <el-button type="success" :loading="warpDialog.creating" @click="createWarp">创建</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import {
  CirclePlus,
  CollectionTag,
  Delete,
  Edit,
  FolderOpened,
  MagicStick,
  Refresh,
  RefreshRight,
  Setting,
  Upload,
  VideoPlay,
} from '@element-plus/icons-vue'
import { panelApi } from '@/api/panel'
import WgcfWarpPanel from '@/components/WgcfWarpPanel.vue'
import type {
  GlobalProxySettings,
  NetworkEgress,
  OutboundProxy,
  ProxyCategory,
  ProxyKind,
  ProxyProtocol,
  GlobalProxySourceMode,
  SaveOutboundProxyRequest,
  SaveGlobalProxySettingsRequest,
  WarpProxyProtocol,
  WarpRuntimeStatus,
} from '@/api/types'
import { formatTime } from '@/utils/format'
import { ipVersionLabel, isWarpConnected, warpStatusLabel } from '@/utils/networkEgress'

const loading = ref(false)
const egressLoading = ref(false)
const proxies = ref<OutboundProxy[]>([])
const proxyCategories = ref<ProxyCategory[]>([])
const usageFilter = ref<'all' | 'used' | 'unused'>('all')
const categoryFilter = ref<number | 'all' | 'uncategorized'>('all')
const selectedProxies = ref<OutboundProxy[]>([])
const proxyTableRef = ref<{ clearSelection: () => void } | null>(null)
const panelEgress = ref<NetworkEgress | null>(null)
const panelEgressError = ref('')
const globalProxy = ref<GlobalProxySettings | null>(null)
const globalProxyLoading = ref(false)
const globalProxyError = ref('')
const warpStatus = ref<WarpRuntimeStatus | null>(null)
const warpStatusError = ref('')
const proxyCategoryLoading = ref(false)
const testingIds = reactive(new Set<number>())
const refreshingIds = reactive(new Set<number>())
const deletingIds = reactive(new Set<number>())
const batchDeleting = ref(false)
const refreshingAllWarps = ref(false)
const AUTO_STATUS_REFRESH_MS = 30_000
let autoStatusRefreshTimer: ReturnType<typeof setInterval> | null = null
let proxyListOperationToken = 0
let panelEgressOperationToken = 0
let globalProxyOperationToken = 0
let warpStatusOperationToken = 0
let proxySaveOperationToken = 0
let proxyImportOperationToken = 0
let warpCreateOperationToken = 0

type DialogCloseDone = () => void

const blankGlobalProxyForm = (): SaveGlobalProxySettingsRequest => ({
  enabled: false,
  sourceMode: 'existing',
  proxyId: null,
  protocol: 'socks5',
  server: '',
  port: 1080,
  username: '',
  password: '',
  secret: '',
  clearPassword: false,
})

const globalProxyDialog = reactive({
  visible: false,
  saving: false,
  hasPassword: false,
  hasSecret: false,
  form: blankGlobalProxyForm(),
})

const batchCategoryDialog = reactive({
  visible: false,
  saving: false,
  categoryId: null as number | null,
  proxyIds: [] as number[],
})

const categoryManagerDialog = reactive({
  visible: false,
  loading: false,
})

const categoryEditorDialog = reactive({
  visible: false,
  saving: false,
  id: null as number | null,
  form: {
    name: '',
    color: '#409eff' as string | null,
    description: '',
  },
})

const blankProxyForm = (): SaveOutboundProxyRequest => ({
  name: '',
  kind: 'manual',
  protocol: 'socks5',
  host: '',
  port: 1080,
  username: '',
  password: '',
  clearPassword: false,
  secret: '',
  resinPlatform: '',
  resinAdminUrl: '',
  resinAdminToken: '',
  clearResinAdminToken: false,
  categoryId: null,
  isEnabled: true,
  testAfterSave: true,
})

const proxyDialog = reactive({
  visible: false,
  saving: false,
  id: null as number | null,
  hasPassword: false,
  hasSecret: false,
  hasResinAdminToken: false,
  selectForGlobal: false,
  form: blankProxyForm(),
})

const importDialog = reactive({
  visible: false,
  importing: false,
  text: '',
  testAfterImport: false,
})

const warpDialog = reactive({
  visible: false,
  creating: false,
  name: '',
  requestId: '',
  protocol: 'http' as WarpProxyProtocol,
  selectForGlobal: false,
})

const warpUnavailable = computed(() => !warpStatus.value
  || !warpStatus.value.platformSupported
  || !warpStatus.value.enabled
  || !warpStatus.value.dockerAvailable)

const maintenance = computed(() => warpStatus.value?.maintenance ?? null)
const hasEnabledWarp = computed(() => proxies.value.some((proxy) => proxy.kind === 'warp' && proxy.isEnabled))
const usageCounts = computed(() => {
  const used = proxies.value.filter(proxyIsUsed).length
  return { all: proxies.value.length, used, unused: proxies.value.length - used }
})
const filteredProxies = computed(() => proxies.value.filter((proxy) => {
  if (usageFilter.value === 'used' && !proxyIsUsed(proxy)) return false
  if (usageFilter.value === 'unused' && proxyIsUsed(proxy)) return false
  if (categoryFilter.value === 'uncategorized' && proxy.category) return false
  if (typeof categoryFilter.value === 'number' && proxy.category?.id !== categoryFilter.value) return false
  return true
}))
const globalProxyGroups = computed(() => ([
  { kind: 'wireguard_warp' as const, label: '外部 WireGuard WARP', items: proxies.value.filter((proxy) => proxy.kind === 'wireguard_warp' && !proxy.managedWgcfProfile) },
  { kind: 'manual' as const, label: '普通代理', items: proxies.value.filter((proxy) => proxy.kind === 'manual') },
  { kind: 'resin' as const, label: 'Resin 动态代理', items: proxies.value.filter((proxy) => proxy.kind === 'resin') },
  { kind: 'warp' as const, label: '受管 WARP', items: proxies.value.filter((proxy) => proxy.kind === 'warp') },
]).filter((group) => group.items.length > 0))
const globalSelectedProxy = computed(() => proxies.value.find(
  (proxy) => proxy.id === globalProxyDialog.form.proxyId,
) ?? null)
const globalProxyLabel = computed(() => {
  if (globalProxyLoading.value && !globalProxy.value) return '读取中'
  if (globalProxyError.value) return '读取失败'
  if (!globalProxy.value?.enabled) return '未启用'
  if (globalProxy.value.sourceMode === 'existing') {
    return globalProxy.value.proxyName
      || proxies.value.find((proxy) => proxy.id === globalProxy.value?.proxyId)?.name
      || '已有代理'
  }
  return protocolLabel(globalProxy.value.protocol)
})
const globalProxyHelp = computed(() => {
  if (globalProxyError.value) return globalProxyError.value
  if (!globalProxy.value?.enabled) return '尚未启用账号全局代理，点击进行配置'
  if (globalProxy.value.sourceMode === 'existing') {
    const selected = proxies.value.find((proxy) => proxy.id === globalProxy.value?.proxyId)
    return selected
      ? `${kindLabel(selected.kind)} · ${selected.name} · ${selected.host}:${selected.port}`
      : globalProxy.value.proxyName
        ? `${globalProxy.value.proxyKind ? kindLabel(globalProxy.value.proxyKind) : '已有代理'} · ${globalProxy.value.proxyName}`
        : '当前引用的已有代理不可用，请重新选择'
  }
  return `${protocolLabel(globalProxy.value.protocol)} · ${globalProxy.value.server}:${globalProxy.value.port}`
})

const maintenanceStatusText = computed(() => {
  if (!maintenance.value?.enabled) return '自动维护未启用'
  return maintenance.value.running ? '正在自动巡检' : '自动维护已启用'
})

const maintenanceStatusType = computed(() => {
  if (!maintenance.value?.enabled) return 'info'
  if (maintenance.value.lastError || maintenance.value.failedCount > 0) return 'warning'
  return 'success'
})

const warpStatusText = computed(() => {
  if (warpStatusError.value) return '检测失败'
  if (!warpStatus.value) return '检测中'
  if (!warpStatus.value.platformSupported) return '平台不支持'
  if (!warpStatus.value.enabled) return '未启用'
  if (!warpStatus.value.dockerAvailable) return 'Docker 不可用'
  return warpStatus.value.dockerVersion ? `可用 · ${warpStatus.value.dockerVersion}` : '可用'
})

const warpStatusType = computed(() => {
  if (warpStatusError.value) return 'danger'
  if (!warpStatus.value) return 'info'
  return warpUnavailable.value ? 'danger' : 'success'
})

const panelEgressText = computed(() => {
  if (egressLoading.value) return '检测中'
  if (panelEgressError.value) return '检测失败'
  if (!panelEgress.value) return '尚未检测'
  if (!panelEgress.value.success) return '检测失败'
  return panelEgress.value.ip || '未知'
})

const panelEgressMeta = computed(() => {
  if (egressLoading.value) return '正在检测网络出口'
  if (panelEgressError.value) return panelEgressError.value
  return [ipVersionLabel(panelEgress.value?.ip), egressLocation(panelEgress.value)]
    .filter(Boolean)
    .join(' · ')
})

const panelWarpStatusHelp = computed(() => {
  const status = panelEgress.value?.warpStatus
  if (isWarpConnected(status)) {
    return '这里只表示面板服务自身的公网出口已使用 Cloudflare WARP；下方代理仍会分别检测。'
  }
  return '“未使用 WARP”只表示面板服务自身直连，不代表下方独立 WARP 代理失效。'
})

function normalizeOptional(value?: string | null) {
  const normalized = value?.trim()
  return normalized || null
}

function kindLabel(kind: ProxyKind) {
  if (kind === 'wireguard_warp') return 'WireGuard WARP'
  if (kind === 'resin') return 'Resin'
  if (kind === 'warp') return '受管 WARP'
  return '普通'
}

function kindTagType(kind: ProxyKind) {
  if (kind === 'warp') return 'success'
  if (kind === 'wireguard_warp') return 'primary'
  if (kind === 'resin') return 'warning'
  return 'info'
}

function protocolLabel(protocol: ProxyProtocol) {
  if (protocol === 'socks5') return 'SOCKS5'
  if (protocol === 'mtproto') return 'MTProto'
  return 'HTTP'
}

function formatDuration(minutes: number) {
  if (minutes > 0 && minutes % 1440 === 0) return `${minutes / 1440} 天`
  if (minutes > 0 && minutes % 60 === 0) return `${minutes / 60} 小时`
  return `${minutes} 分钟`
}

function warpRuntimeStatusLabel(status?: string | null) {
  const value = status?.trim().toLowerCase()
  if (value === 'active' || value === 'healthy') return '正常'
  if (value === 'degraded' || value === 'unhealthy') return '异常'
  if (value === 'recovering' || value === 'restarting') return '恢复中'
  if (value === 'starting' || value === 'creating') return '启动中'
  if (value === 'stopped') return '已停止'
  if (value === 'missing') return '容器丢失'
  if (value === 'failed' || value === 'cleanup_pending') return '故障'
  if (value === 'deleting' || value === 'deleted') return '删除中'
  return '状态未知'
}

function warpRuntimeStatusType(status?: string | null) {
  const value = status?.trim().toLowerCase()
  if (value === 'active' || value === 'healthy') return 'success'
  if (value === 'recovering' || value === 'restarting' || value === 'starting' || value === 'creating') return 'warning'
  if (value === 'degraded' || value === 'unhealthy' || value === 'missing' || value === 'failed' || value === 'cleanup_pending') return 'danger'
  return 'info'
}

function warpRuntimeHelp(proxy: OutboundProxy) {
  const status = warpRuntimeStatusLabel(proxy.warpRuntimeStatus)
  const failureText = (proxy.warpConsecutiveFailures ?? 0) > 0
    ? `，已连续失败 ${proxy.warpConsecutiveFailures} 次`
    : ''
  return `最近维护状态：${status}${failureText}。期望状态：${proxy.isEnabled ? '启用' : '停用'}。`
}

function testStatusLabel(status?: string | null) {
  const value = status?.toLowerCase()
  if (value === 'success' || value === 'ok' || value === 'passed') return '可用'
  if (value === 'testing' || value === 'pending') return '检测中'
  if (value === 'fail' || value === 'failed' || value === 'error') return '失败'
  return '未检测'
}

function testStatusType(status?: string | null) {
  const value = status?.toLowerCase()
  if (value === 'success' || value === 'ok' || value === 'passed') return 'success'
  if (value === 'testing' || value === 'pending') return 'warning'
  if (value === 'fail' || value === 'failed' || value === 'error') return 'danger'
  return 'info'
}

function warpProxyStatusType(status?: string | null) {
  if (!status) return 'info'
  return isWarpConnected(status) ? 'success' : 'danger'
}

function egressIpText(proxy: OutboundProxy) {
  if (proxy.egressIp) return proxy.egressIp
  const status = proxy.testStatus?.toLowerCase()
  return status === 'fail' || status === 'failed' || status === 'error'
    ? '检测失败'
    : '尚未检测'
}

function proxyUsageCount(proxy: OutboundProxy) {
  return proxy.usageCount ?? proxy.accountCount ?? 0
}

function proxyIsUsed(proxy: OutboundProxy) {
  return proxy.isInUse ?? proxyUsageCount(proxy) > 0
}

function egressIpVersion(proxy: OutboundProxy) {
  return ipVersionLabel(proxy.egressIp)
}

function egressRegionText(proxy: OutboundProxy) {
  return [proxy.egressCountry, proxy.egressCity].filter(Boolean).join(' / ') || '未知'
}

function categoryTagStyle(category: ProxyCategory) {
  if (!category.color) return undefined
  return { borderColor: category.color, color: category.color }
}

function categoryProxyCount(category: ProxyCategory) {
  return category.proxyCount ?? proxies.value.filter((proxy) => proxy.category?.id === category.id).length
}

function isSelectableProxy(proxy: OutboundProxy) {
  return !proxy.managedWgcfProfile
}

function onProxySelectionChange(rows: OutboundProxy[]) {
  selectedProxies.value = rows.filter(isSelectableProxy)
}

function egressLocation(egress?: NetworkEgress | null) {
  if (!egress) return '尚未检测网络出口'
  if (!egress.success) return egress.error || '无法获取出口信息'
  return [egress.country, egress.city, egress.isp].filter(Boolean).join(' / ') || '位置未知'
}

function beforeProxyDialogClose(done: DialogCloseDone) {
  if (proxyDialog.saving) return
  resetProxyDialog()
  done()
}

function beforeGlobalProxyDialogClose(done: DialogCloseDone) {
  if (globalProxyDialog.saving) return
  resetGlobalProxyDialog()
  done()
}

function beforeImportDialogClose(done: DialogCloseDone) {
  if (importDialog.importing) return
  resetImportDialog()
  done()
}

function beforeWarpDialogClose(done: DialogCloseDone) {
  if (warpDialog.creating) return
  warpDialog.selectForGlobal = false
  done()
}

async function loadProxies(showLoading = true) {
  const operationToken = ++proxyListOperationToken
  if (showLoading) loading.value = true
  try {
    const result = await panelApi.proxies()
    if (operationToken === proxyListOperationToken) proxies.value = result
  } finally {
    if (operationToken === proxyListOperationToken) loading.value = false
  }
}

async function loadPanelEgress() {
  const operationToken = ++panelEgressOperationToken
  egressLoading.value = true
  panelEgressError.value = ''
  try {
    const result = await panelApi.networkEgress()
    if (operationToken === panelEgressOperationToken) panelEgress.value = result
  } catch (error) {
    if (operationToken === panelEgressOperationToken) {
      panelEgress.value = null
      panelEgressError.value = error instanceof Error ? error.message : '无法检测面板公网出口'
    }
  } finally {
    if (operationToken === panelEgressOperationToken) egressLoading.value = false
  }
}

async function loadProxyCategories(showLoading = false) {
  if (showLoading) categoryManagerDialog.loading = true
  proxyCategoryLoading.value = true
  try {
    proxyCategories.value = await panelApi.proxyCategories()
  } finally {
    proxyCategoryLoading.value = false
    if (showLoading) categoryManagerDialog.loading = false
  }
}

async function loadGlobalProxy(showLoading = true) {
  const operationToken = ++globalProxyOperationToken
  if (showLoading) globalProxyLoading.value = true
  globalProxyError.value = ''
  try {
    const result = await panelApi.globalProxySettings()
    if (operationToken !== globalProxyOperationToken) return false
    globalProxy.value = result
    return true
  } catch (error) {
    if (operationToken === globalProxyOperationToken) {
      globalProxyError.value = error instanceof Error ? error.message : '无法读取全局代理配置'
    }
    return false
  } finally {
    if (operationToken === globalProxyOperationToken) globalProxyLoading.value = false
  }
}

async function loadWarpStatus() {
  const operationToken = ++warpStatusOperationToken
  warpStatusError.value = ''
  try {
    const result = await panelApi.warpStatus()
    if (operationToken === warpStatusOperationToken) warpStatus.value = result
  } catch (error) {
    if (operationToken === warpStatusOperationToken) {
      warpStatus.value = null
      warpStatusError.value = error instanceof Error ? error.message : '无法读取 WARP 运行状态'
    }
  }
}

async function refreshAll() {
  await Promise.allSettled([loadProxies(), loadProxyCategories(), loadGlobalProxy(), loadWarpStatus()])
}

async function refreshWarp(proxy: OutboundProxy) {
  if (refreshingIds.has(proxy.id) || refreshingAllWarps.value) return
  if ((proxy.accountCount ?? 0) > 0) {
    try {
      await ElMessageBox.confirm(
        `刷新“${proxy.name}”会让已绑定的 ${proxy.accountCount} 个账号短暂重连；账号不会回退直连。是否继续？`,
        '确认刷新 WARP',
        { type: 'warning', confirmButtonText: '刷新并复测', cancelButtonText: '取消' },
      )
    } catch {
      return
    }
  }

  refreshingIds.add(proxy.id)
  try {
    const result = await panelApi.refreshWarpProxy(proxy.id)
    if (result.success) ElMessage.success(result.summary || `WARP“${proxy.name}”已恢复`)
    else ElMessage.error(result.error || result.summary || `WARP“${proxy.name}”恢复失败`)
  } finally {
    await Promise.allSettled([loadProxies(), loadWarpStatus()])
    refreshingIds.delete(proxy.id)
  }
}

async function refreshAllWarps() {
  if (refreshingAllWarps.value || refreshingIds.size > 0 || !hasEnabledWarp.value) return
  const enabledWarps = proxies.value.filter((proxy) => proxy.kind === 'warp' && proxy.isEnabled)
  const affectedAccounts = enabledWarps.reduce((total, proxy) => total + (proxy.accountCount ?? 0), 0)
  const accountHint = affectedAccounts > 0
    ? `，${affectedAccounts} 个绑定账号会短暂重连，但不会回退直连`
    : ''
  try {
    await ElMessageBox.confirm(
      `将依次重启并复测 ${enabledWarps.length} 个 WARP${accountHint}。是否继续？`,
      '确认刷新全部 WARP',
      { type: 'warning', confirmButtonText: '全部刷新', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  refreshingAllWarps.value = true
  try {
    const result = await panelApi.refreshAllWarpProxies()
    const summary = `已处理 ${result.checked} 个：正常 ${result.healthy}，恢复 ${result.recovered}，失败 ${result.failed}`
    if (result.failed > 0) ElMessage.warning(summary)
    else ElMessage.success(summary)
  } finally {
    await Promise.allSettled([loadProxies(), loadWarpStatus()])
    refreshingAllWarps.value = false
  }
}

function openBatchCategory() {
  if (selectedProxies.value.length === 0 || batchCategoryDialog.saving) return
  batchCategoryDialog.proxyIds = selectedProxies.value.map((proxy) => proxy.id)
  batchCategoryDialog.categoryId = null
  batchCategoryDialog.visible = true
}

async function saveBatchCategory() {
  if (batchCategoryDialog.saving || batchCategoryDialog.proxyIds.length === 0) return
  const proxyIds = [...batchCategoryDialog.proxyIds]
  const categoryId = batchCategoryDialog.categoryId
  batchCategoryDialog.saving = true
  try {
    const result = await panelApi.batchSetProxyCategory(proxyIds, categoryId)
    ElMessage.success(result.message || `已更新 ${proxyIds.length} 个代理的分类`)
    batchCategoryDialog.visible = false
    batchCategoryDialog.proxyIds = []
    await Promise.all([loadProxies(), loadProxyCategories()])
    proxyTableRef.value?.clearSelection()
    selectedProxies.value = []
  } finally {
    batchCategoryDialog.saving = false
  }
}

async function removeSelectedProxies() {
  if (batchDeleting.value || selectedProxies.value.length === 0) return
  if (selectedProxies.value.some((proxy) => !isSelectableProxy(proxy))) return
  const selected = [...selectedProxies.value]
  const proxyIds = selected.map((proxy) => proxy.id)
  try {
    await ElMessageBox.confirm(
      `确定删除选中的 ${proxyIds.length} 个代理吗？已绑定账号或正在作为全局代理使用的项目会逐项失败并保留。`,
      '确认批量删除',
      { type: 'warning', confirmButtonText: '批量删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  batchDeleting.value = true
  proxyIds.forEach((id) => deletingIds.add(id))
  try {
    const result = await panelApi.batchDeleteProxies(proxyIds)
    if (result.failed > 0) {
      const failures = result.items
        .filter((item) => !item.success)
        .slice(0, 5)
        .map((item) => `#${item.proxyId}：${item.error || item.summary}`)
        .join('；')
      ElMessage.warning(`已删除 ${result.success} 个，${result.failed} 个失败${failures ? `：${failures}` : ''}`)
    } else {
      ElMessage.success(`已删除 ${result.success} 个代理`)
    }
  } finally {
    await Promise.allSettled([loadProxies(), loadProxyCategories()])
    proxyTableRef.value?.clearSelection()
    selectedProxies.value = []
    proxyIds.forEach((id) => deletingIds.delete(id))
    batchDeleting.value = false
  }
}

async function openCategoryManager() {
  categoryManagerDialog.visible = true
  await loadProxyCategories(true)
}

function resetCategoryEditor() {
  categoryEditorDialog.id = null
  categoryEditorDialog.form = {
    name: '',
    color: '#409eff',
    description: '',
  }
}

function openCategoryEditor(category?: ProxyCategory) {
  resetCategoryEditor()
  if (category) {
    categoryEditorDialog.id = category.id
    categoryEditorDialog.form = {
      name: category.name,
      color: category.color || '#409eff',
      description: category.description || '',
    }
  }
  categoryEditorDialog.visible = true
}

async function saveCategory() {
  if (categoryEditorDialog.saving) return
  const name = categoryEditorDialog.form.name.trim()
  if (!name) {
    ElMessage.warning('请填写分类名称')
    return
  }
  const duplicated = proxyCategories.value.some((category) => (
    category.id !== categoryEditorDialog.id
    && category.name.trim().toLocaleLowerCase() === name.toLocaleLowerCase()
  ))
  if (duplicated) {
    ElMessage.warning('已存在同名代理分类')
    return
  }

  const payload = {
    name,
    color: normalizeOptional(categoryEditorDialog.form.color),
    description: normalizeOptional(categoryEditorDialog.form.description),
  }
  categoryEditorDialog.saving = true
  try {
    if (categoryEditorDialog.id) {
      await panelApi.updateProxyCategory(categoryEditorDialog.id, payload)
      ElMessage.success('代理分类已更新')
    } else {
      await panelApi.createProxyCategory(payload)
      ElMessage.success('代理分类已创建')
    }
    categoryEditorDialog.visible = false
    resetCategoryEditor()
    await loadProxyCategories()
  } finally {
    categoryEditorDialog.saving = false
  }
}

async function removeCategory(category: ProxyCategory) {
  try {
    await ElMessageBox.confirm(
      `确定删除代理分类“${category.name}”吗？分类标签会从代理移除，代理本身不会被删除。`,
      '确认删除分类',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  const result = await panelApi.deleteProxyCategory(category.id)
  ElMessage.success(result.message || '代理分类已删除')
  if (categoryFilter.value === category.id) categoryFilter.value = 'all'
  await Promise.allSettled([loadProxyCategories(), loadProxies()])
}

async function openGlobalProxyDialog() {
  if (globalProxyDialog.saving) return
  if (!await loadGlobalProxy()) {
    ElMessage.error(globalProxyError.value || '无法读取全局代理配置，请稍后重试')
    return
  }
  const current = globalProxy.value
  const sourceMode: GlobalProxySourceMode = current?.sourceMode === 'existing' || current?.proxyId
    ? 'existing'
    : current?.server
      ? 'manual'
      : 'existing'
  globalProxyDialog.hasPassword = Boolean(current?.hasPassword)
  globalProxyDialog.hasSecret = Boolean(current?.hasSecret)
  globalProxyDialog.form = current
    ? {
        enabled: current.enabled,
        sourceMode,
        proxyId: current.proxyId ?? null,
        protocol: current.protocol,
        server: current.server || '',
        port: current.port || 1080,
        username: current.username || '',
        password: '',
        secret: '',
        clearPassword: false,
      }
    : blankGlobalProxyForm()
  globalProxyDialog.visible = true
}

function resetGlobalProxyDialog() {
  globalProxyDialog.hasPassword = false
  globalProxyDialog.hasSecret = false
  globalProxyDialog.form = blankGlobalProxyForm()
}

function closeGlobalProxyDialog() {
  if (globalProxyDialog.saving) return
  resetGlobalProxyDialog()
  globalProxyDialog.visible = false
}

async function saveGlobalProxy() {
  if (globalProxyDialog.saving) return
  const form = { ...globalProxyDialog.form }
  if (form.enabled) {
    if (form.sourceMode === 'existing') {
      const selected = proxies.value.find((proxy) => proxy.id === form.proxyId)
      if (!selected) {
        ElMessage.warning('请选择一个已有代理')
        return
      }
      if (selected.managedWgcfProfile) {
        ElMessage.warning('轻量 WARP 出口只支持绑定单个已有账号')
        return
      }
      if (!selected.isEnabled) {
        ElMessage.warning('所选代理已停用，请先启用或选择其他代理')
        return
      }
      if (selected.kind === 'wireguard_warp' && (selected.testStatus !== 'ok' || !selected.egressIp)) {
        ElMessage.warning('外部 WireGuard WARP 端点必须先检测成功后才能作为全局代理')
        return
      }
    } else {
      if (!form.server.trim() || form.port < 1 || form.port > 65535) {
        ElMessage.warning('请填写有效的全局代理主机和端口')
        return
      }
      if (form.protocol === 'mtproto' && !form.secret.trim() && !globalProxyDialog.hasSecret) {
        ElMessage.warning('请填写 MTProxy Secret')
        return
      }
    }
  }

  const payload: SaveGlobalProxySettingsRequest = {
    ...form,
    sourceMode: form.sourceMode,
    proxyId: form.sourceMode === 'existing' ? form.proxyId : null,
    server: form.server.trim(),
    username: form.username.trim(),
    password: form.password.trim(),
    secret: form.secret.trim(),
  }
  globalProxyDialog.saving = true
  try {
    const result = await panelApi.saveGlobalProxySettings(payload)
    ElMessage.success(result.message || (payload.enabled ? '全局代理已保存' : '全局代理已停用'))
    globalProxyDialog.visible = false
    resetGlobalProxyDialog()
    await loadGlobalProxy()
  } finally {
    globalProxyDialog.saving = false
  }
}

function openCreate() {
  if (proxyDialog.saving) return
  proxySaveOperationToken += 1
  proxyDialog.id = null
  proxyDialog.form = blankProxyForm()
  proxyDialog.hasPassword = false
  proxyDialog.hasSecret = false
  proxyDialog.hasResinAdminToken = false
  proxyDialog.selectForGlobal = false
  proxyDialog.visible = true
}

function openProxyCreateForGlobal(kind: 'manual' | 'resin' | 'wireguard_warp') {
  openCreate()
  proxyDialog.selectForGlobal = true
  proxyDialog.form.kind = kind
  if (kind === 'resin') {
    proxyDialog.form.protocol = 'socks5'
    proxyDialog.form.resinPlatform = 'telegram-panel'
  }
}

function resetProxyDialog() {
  proxyDialog.id = null
  proxyDialog.hasPassword = false
  proxyDialog.hasSecret = false
  proxyDialog.hasResinAdminToken = false
  proxyDialog.selectForGlobal = false
  proxyDialog.form = blankProxyForm()
}

function closeProxyDialog() {
  if (proxyDialog.saving) return
  resetProxyDialog()
  proxyDialog.visible = false
}

function openEdit(proxy: OutboundProxy) {
  if (proxyDialog.saving || proxy.managedWgcfProfile) return
  proxySaveOperationToken += 1
  proxyDialog.id = proxy.id
  proxyDialog.hasPassword = Boolean(proxy.hasPassword)
  proxyDialog.hasSecret = Boolean(proxy.hasSecret)
  proxyDialog.hasResinAdminToken = Boolean(proxy.hasResinAdminToken)
  proxyDialog.form = {
    name: proxy.name,
    kind: proxy.kind === 'resin' ? 'resin' : proxy.kind === 'wireguard_warp' ? 'wireguard_warp' : 'manual',
    protocol: proxy.protocol,
    host: proxy.host,
    port: proxy.port,
    username: proxy.username || '',
    password: '',
    clearPassword: false,
    secret: '',
    resinPlatform: proxy.resinPlatform || '',
    resinAdminUrl: proxy.resinAdminUrl || '',
    resinAdminToken: '',
    clearResinAdminToken: false,
    categoryId: proxy.category?.id ?? null,
    isEnabled: proxy.isEnabled,
    testAfterSave: false,
  }
  proxyDialog.visible = true
}

function openImportDialog() {
  if (importDialog.importing) return
  proxyImportOperationToken += 1
  resetImportDialog()
  importDialog.visible = true
}

function resetImportDialog() {
  importDialog.text = ''
  importDialog.testAfterImport = false
}

function closeImportDialog() {
  if (importDialog.importing) return
  resetImportDialog()
  importDialog.visible = false
}
function onProxyKindChange(kind: string | number | boolean | undefined) {
  if ((kind === 'resin' || kind === 'wireguard_warp') && proxyDialog.form.protocol === 'mtproto') {
    proxyDialog.form.protocol = 'socks5'
  }
}

async function saveProxy() {
  if (proxyDialog.saving) return
  const form = { ...proxyDialog.form }
  const editingId = proxyDialog.id
  const selectForGlobal = proxyDialog.selectForGlobal
  if (!form.name.trim() || !form.host.trim() || !form.port) {
    ElMessage.warning('请填写名称、主机和端口')
    return
  }
  if (form.kind === 'resin' && !form.resinPlatform?.trim()) {
    ElMessage.warning('请填写 Resin 平台标识')
    return
  }
  if (form.protocol === 'mtproto' && !form.secret?.trim() && !proxyDialog.hasSecret) {
    ElMessage.warning('请填写 MTProto Secret')
    return
  }

  const payload: SaveOutboundProxyRequest = {
    ...form,
    name: form.name.trim(),
    host: form.host.trim(),
    // 空字符串表示显式清空；null 在更新接口中表示沿用旧值。
    username: form.username?.trim() ?? '',
    password: normalizeOptional(form.password),
    clearPassword: Boolean(form.clearPassword),
    secret: normalizeOptional(form.secret),
    resinPlatform: form.kind === 'resin' ? normalizeOptional(form.resinPlatform) : null,
    resinAdminUrl: form.kind === 'resin' ? form.resinAdminUrl?.trim() ?? '' : null,
    resinAdminToken: form.kind === 'resin' ? normalizeOptional(form.resinAdminToken) : null,
    clearResinAdminToken: form.kind === 'resin' && Boolean(form.clearResinAdminToken),
  }

  const operationToken = ++proxySaveOperationToken
  proxyDialog.saving = true
  try {
    const saved = editingId
      ? await panelApi.updateProxy(editingId, payload)
      : await panelApi.createProxy(payload)
    if (operationToken !== proxySaveOperationToken) return
    const passed = ['success', 'ok', 'passed'].includes(saved.testStatus.toLowerCase())
    if (payload.testAfterSave && !passed) {
      ElMessage.warning(saved.lastError || `${editingId ? '代理已更新' : '代理已创建'}，但出口检测失败`)
    } else {
      ElMessage.success(editingId ? '代理已更新' : '代理已创建')
    }
    proxyDialog.visible = false
    resetProxyDialog()
    if (selectForGlobal) {
      globalProxyDialog.form.sourceMode = 'existing'
      globalProxyDialog.form.proxyId = saved.id
    }
  } finally {
    await Promise.allSettled([loadProxies()])
    if (operationToken === proxySaveOperationToken) proxyDialog.saving = false
  }
}

async function testProxy(proxy: OutboundProxy) {
  testingIds.add(proxy.id)
  try {
    const result = await panelApi.testProxy(proxy.id)
    const passed = ['success', 'ok', 'passed'].includes(result.testStatus.toLowerCase())
    if (passed) {
      const location = egressRegionText(result)
      const isp = result.egressIsp || '未知'
      ElMessage.success(`出口 ${result.egressIp || '检测成功'} · ${location} · ISP ${isp} · ${result.lastLatencyMs ?? '-'} ms`)
    }
    else ElMessage.error(result.lastError || '代理检测失败')
    await loadProxies()
  } finally {
    testingIds.delete(proxy.id)
  }
}

async function removeProxy(proxy: OutboundProxy) {
  if (deletingIds.has(proxy.id) || proxy.managedWgcfProfile) return
  try {
    await ElMessageBox.confirm(
      `确定删除代理“${proxy.name}”吗？已绑定账号需要先切换为直连或其他代理。`,
      '确认删除',
      { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  if (deletingIds.has(proxy.id)) return
  deletingIds.add(proxy.id)
  try {
    const result = await panelApi.deleteProxy(proxy.id)
    ElMessage.success(result.message || '代理已删除')
  } finally {
    await Promise.allSettled([loadProxies()])
    deletingIds.delete(proxy.id)
  }
}

async function importProxyText() {
  if (importDialog.importing) return
  const text = importDialog.text.trim()
  if (!text) {
    ElMessage.warning('请填写代理文本')
    return
  }
  const operationToken = ++proxyImportOperationToken
  importDialog.importing = true
  try {
    const imported = await panelApi.importProxies({ text, testAfterImport: importDialog.testAfterImport })
    if (operationToken !== proxyImportOperationToken) return
    ElMessage.success(`已导入 ${imported.length} 个代理`)
    importDialog.visible = false
    resetImportDialog()
  } finally {
    // 超时或取消时后端可能已完成前半批，始终重新读取实际列表。
    await Promise.allSettled([loadProxies()])
    if (operationToken === proxyImportOperationToken) importDialog.importing = false
  }
}

function openWarpCreate() {
  openWarpCreateInternal(false)
}

function openWarpCreateForGlobal() {
  openWarpCreateInternal(true)
}

function openWarpCreateInternal(selectForGlobal = false) {
  if (warpDialog.creating) return
  if (warpUnavailable.value) {
    ElMessage.warning(warpStatusError.value || warpStatus.value?.error || '当前环境无法创建 WARP')
    return
  }
  warpCreateOperationToken += 1
  warpDialog.name = ''
  warpDialog.protocol = warpStatus.value?.defaultProtocol === 'socks5' ? 'socks5' : 'http'
  warpDialog.selectForGlobal = selectForGlobal
  warpDialog.requestId = typeof crypto.randomUUID === 'function'
    ? crypto.randomUUID()
    : `warp-${Date.now()}-${Math.random().toString(36).slice(2)}`
  warpDialog.visible = true
}

function closeWarpDialog() {
  if (warpDialog.creating) return
  warpDialog.selectForGlobal = false
  warpDialog.visible = false
}

async function createWarp() {
  if (warpDialog.creating) return
  const operationToken = ++warpCreateOperationToken
  const name = normalizeOptional(warpDialog.name)
  const requestId = warpDialog.requestId
  const selectForGlobal = warpDialog.selectForGlobal
  warpDialog.creating = true
  try {
    const created = await panelApi.createWarpProxies({
      name,
      requestId,
      protocol: warpDialog.protocol,
    })
    if (operationToken !== warpCreateOperationToken) return
    ElMessage.success(`WARP 代理“${created.name}”已创建（${protocolLabel(created.protocol)}）`)
    warpDialog.visible = false
    warpDialog.requestId = ''
    warpDialog.selectForGlobal = false
    if (selectForGlobal) {
      globalProxyDialog.form.sourceMode = 'existing'
      globalProxyDialog.form.proxyId = created.id
    }
  } finally {
    await Promise.allSettled([loadProxies(), loadWarpStatus()])
    if (operationToken === warpCreateOperationToken) warpDialog.creating = false
  }
}

onMounted(() => {
  void Promise.allSettled([loadProxies(), loadProxyCategories(), loadPanelEgress(), loadGlobalProxy(), loadWarpStatus()])
  autoStatusRefreshTimer = setInterval(() => {
    if (document.visibilityState !== 'visible'
      || refreshingAllWarps.value
      || refreshingIds.size > 0
      || deletingIds.size > 0) return
    void Promise.allSettled([loadProxies(false), loadWarpStatus()])
  }, AUTO_STATUS_REFRESH_MS)
})

onBeforeUnmount(() => {
  if (autoStatusRefreshTimer) clearInterval(autoStatusRefreshTimer)
  autoStatusRefreshTimer = null
})
</script>

<style scoped>
.proxies-page {
  width: min(100%, 1536px);
  margin: 0 auto;
}

.network-strip {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 16px;
  padding: 14px 16px;
  border: 1px solid var(--tp-border);
  border-left: 4px solid var(--el-color-primary);
  border-radius: 4px;
  background: var(--tp-panel);
  box-shadow: var(--tp-card-shadow);
}

.network-main,
.network-value,
.warp-runtime,
.form-options {
  display: flex;
  align-items: center;
}

.network-main {
  min-width: 0;
  gap: 12px;
}

.network-icon {
  color: var(--el-color-primary);
  font-size: 30px;
}

.network-label,
.network-meta,
.runtime-error {
  color: var(--tp-muted);
  font-size: 12px;
}

.network-value {
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 2px;
  font-size: 18px;
  font-weight: 600;
}

.network-meta {
  margin-top: 3px;
  overflow-wrap: anywhere;
}

.network-scope-note {
  margin-top: 3px;
  color: var(--tp-muted);
  font-size: 12px;
}

.warp-runtime {
  gap: 8px;
  white-space: nowrap;
}

.runtime-error {
  margin-top: 8px;
  text-align: right;
}

.warp-maintenance {
  display: grid;
  gap: 7px;
  margin-top: 12px;
  padding-top: 12px;
  border-top: 1px solid var(--tp-border);
  color: var(--tp-muted);
  font-size: 12px;
}

.maintenance-state,
.maintenance-schedule {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px 16px;
}

.maintenance-error,
.failure-count {
  color: var(--el-color-danger);
}

.monospace {
  font-family: Consolas, "Microsoft YaHei", monospace;
}

.warp-egress-tag {
  margin-top: 4px;
}

.selected-proxy-summary {
  display: grid;
  gap: 5px;
  margin: -2px 0 14px;
  padding: 12px;
  border: 1px solid var(--tp-border);
  border-radius: 4px;
  background: var(--tp-panel-2);
}

.selected-proxy-heading,
.global-proxy-actions,
.proxy-list-header,
.proxy-list-actions,
.egress-detail {
  display: flex;
  align-items: center;
}

.selected-proxy-heading,
.global-proxy-actions,
.proxy-list-actions {
  flex-wrap: wrap;
  gap: 8px;
}

.global-proxy-actions {
  margin-top: 4px;
}

.proxy-list-header {
  justify-content: space-between;
  gap: 16px;
  margin-bottom: 12px;
}

.usage-tabs {
  flex: 1;
  min-width: 260px;
}

.usage-tabs :deep(.el-tabs__header) {
  margin: 0;
}

.usage-tabs :deep(.el-tabs__content) {
  display: none;
}

.usage-tab-label {
  display: inline-flex;
  align-items: baseline;
  gap: 5px;
}

.usage-tab-label strong {
  color: var(--tp-muted);
  font-size: 12px;
}

.category-filter {
  width: 160px;
}

.egress-detail {
  align-items: flex-start;
  gap: 6px;
  min-height: 20px;
}

.egress-field-label {
  flex: 0 0 30px;
  color: var(--tp-muted);
  font-size: 12px;
}

.category-manager-toolbar {
  margin-bottom: 12px;
}

.form-hint {
  width: 100%;
  margin-top: 6px;
  color: var(--tp-muted);
  font-size: 12px;
  line-height: 1.5;
}

.form-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0 16px;
}

.credential-field {
  display: grid;
  width: 100%;
  gap: 4px;
}

.resin-project-reference {
  display: flex;
  align-items: center;
  gap: 4px;
  margin-bottom: 12px;
  color: var(--tp-muted);
  font-size: 13px;
}

.resin-project-reference :deep(.el-link) {
  font-weight: 600;
}

.form-options {
  justify-content: space-between;
  gap: 16px;
  flex-wrap: wrap;
}

.proxy-table :deep(.cell) {
  overflow-wrap: anywhere;
}

@media (max-width: 640px) {
  :global(.proxy-editor-dialog) {
    display: flex;
    flex-direction: column;
    max-height: calc(100vh - 24px);
    margin: 12px auto !important;
  }

  :global(.proxy-editor-dialog .el-dialog__body) {
    flex: 1;
    min-height: 0;
    overflow-y: auto;
  }

  :global(.proxy-editor-dialog .el-dialog__footer) {
    flex: none;
  }

  .network-strip,
  .network-main {
    align-items: flex-start;
  }

  .form-grid {
    grid-template-columns: 1fr;
  }

  .warp-runtime {
    width: 100%;
    justify-content: space-between;
  }

  .proxy-list-header,
  .proxy-list-actions {
    align-items: stretch;
    flex-direction: column;
  }

  .usage-tabs,
  .category-filter,
  .proxy-list-actions .el-button {
    width: 100%;
  }

  .proxy-list-actions .el-button {
    margin-left: 0;
  }

  .maintenance-state,
  .maintenance-schedule {
    align-items: flex-start;
    flex-direction: column;
    gap: 6px;
  }
}
</style>
