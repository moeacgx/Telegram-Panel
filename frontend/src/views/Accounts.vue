<template>
  <div class="accounts-page">
    <el-card shadow="never" class="page-card">
      <div class="toolbar">
        <el-select v-model="filters.categoryId" placeholder="全部分类" clearable class="filter">
          <el-option label="全部分类" :value="null" />
          <el-option v-for="category in categories" :key="category.id" :label="category.name" :value="category.id" />
        </el-select>
        <el-checkbox v-model="filters.onlyWaste">只看废号</el-checkbox>
        <el-input
          v-model="filters.search"
          placeholder="搜索账号..."
          clearable
          class="search"
          :prefix-icon="Search"
          @keyup.enter="load"
        />
        <el-button type="primary" :icon="Refresh" :loading="loading" @click="reload">刷新列表</el-button>
      </div>
    </el-card>

    <div class="action-bar">
      <el-button :icon="selectionIcon" :disabled="loading || rows.length === 0" @click="cycleSelection">
        {{ selectionText }}
      </el-button>
      <el-button
        :icon="Refresh"
        :loading="selectedStatusRefreshing"
        :disabled="loading || selectedIds.length === 0 || selectedStatusRefreshing"
        @click="batchRefreshStatus"
      >
        刷新已选状态
      </el-button>
      <el-button :icon="Monitor" :disabled="loading || selectedMutationDisabled" @click="batchKickDevices">
        踢出其他设备（已选）
      </el-button>
      <el-button type="danger" :icon="Delete" :disabled="loading || selectedMutationDisabled" @click="cleanupWaste('selected')">
        清理废号（已选）
      </el-button>
      <el-button v-if="filters.onlyWaste" type="danger" :icon="Delete" :disabled="loading || rows.length === 0 || statusRefreshCount > 0" @click="cleanupWaste('filtered')">
        清理废号（筛选）
      </el-button>
      <el-button type="danger" :icon="Delete" :disabled="loading || statusRefreshCount > 0" @click="cleanupWaste('all')">
        清理所有废号
      </el-button>

      <el-dropdown trigger="click" :disabled="loading" @command="handleBatchCommand">
        <el-button :icon="MoreFilled">
          批量操作<el-icon class="el-icon--right"><ArrowDown /></el-icon>
        </el-button>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="batch-join" :disabled="selectedMutationDisabled">批量加群/订阅/启用Bot（已选）</el-dropdown-item>
            <el-dropdown-item command="batch-leave" :disabled="selectedMutationDisabled">批量退群/退订/停用Bot（已选）</el-dropdown-item>
            <el-dropdown-item command="two-factor" :disabled="selectedMutationDisabled">修改二级密码（已选）</el-dropdown-item>
            <el-dropdown-item command="recovery-email" :disabled="selectedMutationDisabled">批量换绑邮箱（找回+登录）（Cloud Mail）（已选）</el-dropdown-item>
            <el-dropdown-item command="kick-devices" :disabled="selectedMutationDisabled">批量踢出所有其他设备（已选）</el-dropdown-item>
            <el-dropdown-item command="category" :disabled="selectedMutationDisabled">批量修改分类（已选）</el-dropdown-item>
            <el-dropdown-item command="proxy" :disabled="selectedMutationDisabled">批量切换代理（已选）</el-dropdown-item>
            <el-dropdown-item command="nickname" :disabled="selectedMutationDisabled">批量改昵称（已选）</el-dropdown-item>
            <el-dropdown-item command="avatar" :disabled="selectedMutationDisabled">批量改头像（已选）</el-dropdown-item>
            <el-dropdown-item command="username" :disabled="selectedMutationDisabled">批量改用户名（已选）</el-dropdown-item>
            <el-dropdown-item command="bio" :disabled="selectedMutationDisabled">批量改Bio（已选）</el-dropdown-item>
            <el-dropdown-item command="delete" :disabled="selectedMutationDisabled">删除已选</el-dropdown-item>
            <el-dropdown-item command="export-selected">导出已选</el-dropdown-item>
            <el-dropdown-item command="export-page">导出当前页</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>

      <ColumnVisibilityMenu
        v-model="visibleColumnKeys"
        :columns="accountColumns"
        :disabled="loading"
        @reset="resetColumns"
        @show-all="showAllColumns"
      />

      <el-tag v-if="selectedIds.length > 0" type="info">已选 {{ selectedIds.length }}</el-tag>
      <el-tag v-if="statusRefreshCount > 0" type="warning">后台刷新中 {{ statusRefreshCount }} 个</el-tag>
      <span v-if="selectedIds.length === 0 && statusRefreshCount === 0" class="muted">共 {{ total }} 个账号</span>
    </div>

    <el-card shadow="never" class="page-card mt-4">
      <el-table
        ref="tableRef"
        v-loading="loading"
        :data="rows"
        stripe
        row-key="id"
        class="accounts-table"
        @selection-change="onSelectionChange"
      >
        <el-table-column type="selection" width="48" reserve-selection />
        <el-table-column v-if="isColumnVisible('phone')" prop="displayPhone" label="手机号" :min-width="isCompactList ? 214 : 150">
          <template #default="{ row }">
            <div>{{ row.displayPhone }}</div>
            <template v-if="isCompactList && isColumnVisible('proxy')">
              <div v-if="row.proxy" class="mobile-account-proxy">
                <el-tag size="small" :type="proxyKindTagType(row.proxy.kind)">{{ proxyKindLabel(row.proxy.kind) }}</el-tag>
                <el-tooltip :content="row.proxy.name || `代理 #${row.proxy.id}`" placement="top">
                  <span class="proxy-name">#{{ row.proxy.id }}</span>
                </el-tooltip>
                <span class="cell-sub">{{ row.proxy.egressIp || '出口未检测' }}</span>
              </div>
              <div v-else class="cell-sub">{{ row.useGlobalProxy ? '全局代理设置' : '直连' }}</div>
            </template>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('proxy') && (!isCompactList || !isColumnVisible('phone'))" label="代理" min-width="170">
          <template #default="{ row }">
            <div v-if="row.proxy" class="proxy-cell">
              <div>
                <el-tag size="small" :type="proxyKindTagType(row.proxy.kind)">{{ proxyKindLabel(row.proxy.kind) }}</el-tag>
                <el-tooltip :content="row.proxy.name || `代理 #${row.proxy.id}`" placement="top">
                  <span class="proxy-name">#{{ row.proxy.id }}</span>
                </el-tooltip>
              </div>
              <div class="cell-sub">{{ row.proxy.egressIp || '出口未检测' }}</div>
            </div>
            <el-tag v-else type="info" size="small">{{ row.useGlobalProxy ? '全局代理设置' : '直连' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('nickname')" prop="nickname" label="昵称" min-width="130">
          <template #default="{ row }">{{ row.nickname || '-' }}</template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('username')" prop="username" label="用户名" min-width="130">
          <template #default="{ row }">{{ row.username ? `@${row.username}` : '-' }}</template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('remark')" prop="remark" label="备注" min-width="170">
          <template #default="{ row }">
            <el-tooltip v-if="row.remark" :content="row.remark" placement="top">
              <span class="ellipsis">{{ row.remark }}</span>
            </el-tooltip>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('userId')" prop="userId" label="用户ID" min-width="120" />
        <el-table-column v-if="isColumnVisible('category')" label="分类" min-width="130">
          <template #default="{ row }">
            <el-tag v-if="row.category" class="account-category-tag" effect="plain" :style="accountCategoryTagStyle(row.category)">
              {{ row.category.name }}
            </el-tag>
            <span v-else>未分类</span>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('chatCounts')" label="频道/群组" width="110">
          <template #default="{ row }">{{ row.channelCount }} / {{ row.groupCount }}</template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('telegramStatus')" label="Telegram 状态" min-width="180">
          <template #default="{ row }">
            <el-tooltip :content="buildStatusTitle(row)" placement="top">
              <el-tag :type="telegramStatusTagType(row)" size="small">{{ telegramStatusText(row) }}</el-tag>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('registrationAt')" label="注册时间（估算，非百分百正确）" min-width="210">
          <template #default="{ row }">{{ formatTime(row.estimatedRegistrationAt, '-') }}</template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('lastSyncAt')" label="最后数据同步" min-width="170">
          <template #default="{ row }">{{ formatTime(row.lastSyncAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" :width="isCompactList ? 70 : 180" fixed="right">
          <template #default="{ row }">
            <div class="row-actions">
              <el-tooltip v-if="!isCompactList" content="查看详情" placement="top">
                <el-button link type="primary" :icon="InfoFilled" @click="openDetails(row)" />
              </el-tooltip>
              <el-tooltip v-if="!isCompactList" content="编辑用户资料" placement="top">
                <el-button link type="primary" :icon="Edit" :disabled="loading || isStatusRefreshing(row.id)" @click="openProfile(row)" />
              </el-tooltip>
              <el-tooltip v-if="!isCompactList" content="刷新 Telegram 状态" placement="top">
                <el-button
                  link
                  type="primary"
                  :icon="Refresh"
                  :loading="isStatusRefreshing(row.id)"
                  :disabled="loading || isStatusRefreshing(row.id)"
                  @click="refreshStatus(row)"
                />
              </el-tooltip>
              <el-dropdown trigger="click" @command="(command: string | number | object) => handleRowCommand(String(command), row)">
                <el-button link :icon="MoreFilled" />
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item v-if="isCompactList" command="details" :icon="InfoFilled">查看详情</el-dropdown-item>
                    <el-dropdown-item v-if="isCompactList" command="profile" :icon="Edit" :disabled="loading || isStatusRefreshing(row.id)">编辑用户资料</el-dropdown-item>
                    <el-dropdown-item
                      v-if="isCompactList"
                      command="refresh"
                      :icon="Refresh"
                      :disabled="loading || isStatusRefreshing(row.id)"
                    >刷新 Telegram 状态</el-dropdown-item>
                    <el-dropdown-item command="join" :icon="UserFilled" :disabled="isStatusRefreshing(row.id)">加群/订阅</el-dropdown-item>
                    <el-dropdown-item command="leave" :icon="SwitchButton" :disabled="isStatusRefreshing(row.id)">退群/退订</el-dropdown-item>
                    <el-dropdown-item command="channels" :icon="Promotion">查看加入的频道</el-dropdown-item>
                    <el-dropdown-item command="groups" :icon="ChatDotRound">查看加入的群组</el-dropdown-item>
                    <el-dropdown-item command="inbox" :icon="Message" :disabled="isStatusRefreshing(row.id)">系统通知（验证码）</el-dropdown-item>
                    <el-dropdown-item command="devices" :icon="Monitor" :disabled="isStatusRefreshing(row.id)">在线设备</el-dropdown-item>
                    <el-dropdown-item divided command="proxy" :icon="Connection" :disabled="isStatusRefreshing(row.id)">切换代理</el-dropdown-item>
                    <el-dropdown-item
                      command="proxy-egress"
                      :icon="Position"
                      :disabled="!row.proxy?.isEnabled || proxyEgressCheckingIds.has(row.id)"
                    >{{ proxyEgressCheckingIds.has(row.id) ? '检测代理出口中' : '检测代理出口' }}</el-dropdown-item>
                    <el-dropdown-item divided command="two-factor" :icon="Lock" :disabled="isStatusRefreshing(row.id)">修改二级密码</el-dropdown-item>
                    <el-dropdown-item command="recovery-email" :icon="Message" :disabled="isStatusRefreshing(row.id)">绑定/换绑找回邮箱</el-dropdown-item>
                    <el-dropdown-item command="login-email" :icon="Message" :disabled="isStatusRefreshing(row.id)">绑定/换绑登录邮箱</el-dropdown-item>
                    <el-dropdown-item divided command="toggle" :icon="SwitchButton" :disabled="isStatusRefreshing(row.id)">{{ row.isActive ? '停用' : '启用' }}</el-dropdown-item>
                    <el-dropdown-item command="delete" :icon="Delete" :disabled="isStatusRefreshing(row.id)">删除账号</el-dropdown-item>
                  </el-dropdown-menu>
                </template>
              </el-dropdown>
            </div>
          </template>
        </el-table-column>
      </el-table>

      <div class="pager">
        <el-pagination
          v-model:current-page="page"
          v-model:page-size="pageSize"
          :total="total"
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next"
          @change="load"
        />
      </div>
    </el-card>

    <el-dialog v-model="details.visible" title="账号详情" width="560px">
      <el-skeleton v-if="details.loading" :rows="6" animated />
      <template v-else-if="details.account">
        <el-descriptions :column="1" border>
          <el-descriptions-item label="注册时间（估算，非百分百正确）">{{ formatTime(details.account.estimatedRegistrationAt, '-') }}</el-descriptions-item>
          <el-descriptions-item label="导入时间">{{ formatTime(details.account.createdAt) }}</el-descriptions-item>
          <el-descriptions-item label="Session 路径">{{ details.account.sessionPath }}</el-descriptions-item>
        </el-descriptions>

        <el-divider />
        <el-form label-position="top">
          <el-form-item label="账号备注">
            <el-input v-model="details.form.remark" type="textarea" :rows="3" maxlength="500" show-word-limit />
          </el-form-item>
          <el-form-item label="当前保存的二级密码">
            <el-input v-model="details.form.twoFactorPassword" :type="details.showPassword ? 'text' : 'password'">
              <template #append>
                <el-button :icon="details.showPassword ? Hide : View" @click="details.showPassword = !details.showPassword" />
              </template>
            </el-input>
          </el-form-item>
        </el-form>
      </template>
      <template #footer>
        <el-button @click="details.visible = false">关闭</el-button>
        <el-button
          type="primary"
          :loading="details.saving"
          :disabled="details.account ? isStatusRefreshing(details.account.id) : false"
          @click="saveDetails"
        >保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="profile.visible" title="编辑用户资料" width="620px">
      <el-form label-position="top">
        <el-alert title="可修改：昵称、Bio、用户名、头像。批量修改请使用账号列表上方的批量操作。" type="info" :closable="false" show-icon />
        <div class="dialog-account">账号：{{ profile.row?.displayPhone }}</div>
        <el-form-item>
          <el-checkbox v-model="profile.form.editNickname">修改昵称</el-checkbox>
          <el-input v-model="profile.form.nickname" :disabled="!profile.form.editNickname" placeholder="昵称将写入 Telegram 的 first_name（last_name 置空）" />
        </el-form-item>
        <el-form-item>
          <el-checkbox v-model="profile.form.editBio">修改 Bio（简介）</el-checkbox>
          <el-input v-model="profile.form.bio" :disabled="!profile.form.editBio" type="textarea" :rows="4" placeholder="留空也会生效（可用于清空 Bio）" />
        </el-form-item>
        <el-form-item>
          <el-checkbox v-model="profile.form.editUsername">修改用户名（t.me/xxx）</el-checkbox>
          <el-input v-model="profile.form.username" :disabled="!profile.form.editUsername" placeholder="只能包含字母/数字/下划线，留空可清空用户名" />
        </el-form-item>
        <el-form-item>
          <el-checkbox v-model="profile.form.editAvatar">上传头像</el-checkbox>
          <el-upload
            :auto-upload="false"
            :limit="1"
            accept="image/*"
            :on-change="onAvatarChange"
            :on-remove="onAvatarRemove"
            :disabled="!profile.form.editAvatar"
          >
            <el-button :icon="Picture" :disabled="!profile.form.editAvatar">选择头像图片</el-button>
          </el-upload>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="profile.visible = false">取消</el-button>
        <el-button type="primary" :loading="profile.saving" @click="saveProfile">保存</el-button>
      </template>
    </el-dialog>

    <BatchChatMembershipDialog ref="batchChatMembershipRef" @completed="onChatMembershipCompleted" />

    <el-dialog v-model="categoryDialog.visible" title="批量修改分类" width="420px">
      <el-select v-model="categoryDialog.categoryId" clearable placeholder="未分类" style="width: 100%">
        <el-option label="未分类" :value="null" />
        <el-option v-for="category in categories" :key="category.id" :label="category.name" :value="category.id" />
      </el-select>
      <template #footer>
        <el-button @click="categoryDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="categoryDialog.saving" @click="saveBatchCategory">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="proxyDialog.visible"
      :title="proxyDialog.title"
      width="min(520px, calc(100vw - 24px))"
      :before-close="beforeAccountProxyDialogClose"
      :close-on-click-modal="!proxyDialog.running"
      :close-on-press-escape="!proxyDialog.running"
      :show-close="!proxyDialog.running"
    >
      <el-form label-position="top" :disabled="proxyDialog.running">
        <el-form-item label="连接方式">
          <el-radio-group v-model="proxyDialog.strategy" class="proxy-route-options">
            <el-radio-button value="direct">直连</el-radio-button>
            <el-radio-button value="global">全局设置</el-radio-button>
            <el-radio-button value="existing">已有代理</el-radio-button>
            <el-radio-button
              value="warp_per_account"
              :disabled="!warpAvailable || proxyDialog.accountIds.length > 10"
            >每账号独立 WARP</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-form-item v-if="proxyDialog.strategy === 'existing'" label="选择代理">
          <el-select v-model="proxyDialog.proxyId" class="full" filterable placeholder="请选择代理">
            <el-option
              v-for="proxy in proxies"
              :key="proxy.id"
              :value="proxy.id"
              :label="`${proxy.name} · ${proxy.protocol.toUpperCase()} · ${proxy.egressIp || `${proxy.host}:${proxy.port}`}`"
              :disabled="!proxy.isEnabled"
            />
          </el-select>
        </el-form-item>
        <el-alert
          v-if="!proxyDialog.strategy"
          title="请明确选择本次账号切换使用的出口；系统不会默认切换为直连"
          type="warning"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <el-alert
          v-else-if="proxyDialog.strategy === 'direct'"
          title="直连会向 Telegram 暴露面板公网 IP；只有确认承担该风险时才应用"
          type="error"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <el-alert
          v-else-if="proxyDialog.strategy === 'global'"
          title="仅在系统已配置有效 Telegram 全局代理时可用；未配置会拒绝保存，不会回退直连"
          type="warning"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <el-alert
          v-if="proxyDialog.strategy === 'warp_per_account'"
          :title="`将创建 ${proxyDialog.accountIds.length} 个独立 WARP 代理，并逐一绑定账号`"
          type="warning"
          :closable="false"
          show-icon
        />
        <div class="muted proxy-account-count">账号数量：{{ proxyDialog.accountIds.length }}</div>
      </el-form>
      <template #footer>
        <el-button :disabled="proxyDialog.running" @click="proxyDialog.visible = false">取消</el-button>
        <el-button type="primary" :loading="proxyDialog.running" @click="saveAccountProxy">应用</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="twoFactor.visible" :title="twoFactor.accountIds.length > 1 ? '批量修改二级密码' : '修改二级密码'" width="560px">
      <el-form label-position="top">
        <el-alert
          title="忘记原二级密码时，可发起重置申请，通常需要等待 7 天。等待结束后再回来设置新的二级密码。"
          type="info"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <el-form-item v-if="twoFactor.accountIds.length > 1">
          <el-switch v-model="twoFactor.form.useStoredPasswords" active-text="使用数据库中保存的原二级密码" />
        </el-form-item>
        <el-alert
          v-if="twoFactor.accountIds.length > 1 && twoFactor.form.useStoredPasswords"
          title="将为每个账号使用其在数据库中保存的二级密码；未保存密码的账号会使用下方统一原密码兜底。"
          type="warning"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <el-form-item :label="twoFactor.accountIds.length > 1 ? '原二级密码（统一/兜底）' : '原二级密码'">
          <el-input v-model="twoFactor.form.currentPassword" type="password" show-password placeholder="账号未开启两步验证时可留空" />
        </el-form-item>
        <el-form-item label="新二级密码">
          <el-input v-model="twoFactor.form.newPassword" type="password" show-password />
        </el-form-item>
        <el-form-item label="确认新二级密码">
          <el-input v-model="twoFactor.form.confirmPassword" type="password" show-password />
        </el-form-item>
        <el-form-item label="密码提示（可选）">
          <el-input v-model="twoFactor.form.hint" />
        </el-form-item>
        <el-form-item>
          <el-checkbox v-model="twoFactor.form.saveNewPasswordToDb">修改成功后将新密码保存到数据库</el-checkbox>
        </el-form-item>
        <div class="muted">账号数量：{{ twoFactor.accountIds.length }}</div>
      </el-form>
      <template #footer>
        <el-button @click="twoFactor.visible = false">关闭</el-button>
        <el-button type="warning" plain :loading="twoFactor.running" @click="requestTwoFactorReset">忘记密码（申请重置）</el-button>
        <el-button type="primary" :loading="twoFactor.running" @click="submitTwoFactor">开始修改</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="emailDialog.visible" :title="emailDialog.title" width="520px">
      <el-form label-position="top">
        <div v-if="emailDialog.row" class="dialog-account">账号：{{ emailDialog.row.displayPhone }}</div>
        <el-alert v-if="emailDialog.statusText" :title="emailDialog.statusText" type="info" :closable="false" show-icon />
        <el-alert
          v-if="emailDialog.kind === 'recovery' && emailDialog.canOpenTwoFactor"
          title="该账号未开启两步验证，无法绑定找回邮箱。请先设置二级密码。"
          type="warning"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <template v-else>
          <el-form-item v-if="emailDialog.kind === 'recovery'" label="原二级密码">
            <el-input v-model="emailDialog.currentPassword" type="password" show-password placeholder="留空时优先使用系统保存的二级密码" />
          </el-form-item>
          <el-form-item :label="emailDialog.kind === 'recovery' ? '新找回邮箱' : '新登录邮箱'">
            <el-input v-model="emailDialog.email" placeholder="name@example.com" />
          </el-form-item>
          <el-form-item label="邮箱验证码">
            <el-input v-model="emailDialog.code" placeholder="收到验证码后填写并确认" />
          </el-form-item>
          <div class="field-help">
            {{
              emailDialog.kind === 'recovery'
                ? '即使发送/重发提示失败，但实际收到了邮件验证码，也可以直接输入验证码确认。'
                : '如果提示不支持新增登录邮箱，将无法在该账号已登录状态下设置。'
            }}
          </div>
        </template>
      </el-form>
      <template #footer>
        <el-button @click="emailDialog.visible = false">关闭</el-button>
        <el-button v-if="emailDialog.kind === 'recovery' && emailDialog.hasPendingRecoveryEmail" :loading="emailDialog.sending" @click="resendRecoveryEmailCode">
          重发验证码
        </el-button>
        <el-button v-if="emailDialog.kind === 'recovery' && emailDialog.hasPendingRecoveryEmail" type="warning" plain :loading="emailDialog.sending" @click="cancelRecoveryEmail">
          取消验证
        </el-button>
        <el-button v-if="emailDialog.kind === 'recovery' && emailDialog.canOpenTwoFactor" type="warning" plain @click="openTwoFactorFromEmail">
          去设置二级密码
        </el-button>
        <el-button v-if="emailDialog.kind === 'login'" :loading="emailDialog.sending" @click="resendLoginEmailCode">
          重发验证码
        </el-button>
        <el-button v-if="!(emailDialog.kind === 'recovery' && emailDialog.canOpenTwoFactor)" :loading="emailDialog.sending" @click="sendEmailCode">
          发送验证码
        </el-button>
        <el-button
          v-if="!(emailDialog.kind === 'recovery' && emailDialog.canOpenTwoFactor)"
          type="primary"
          :loading="emailDialog.confirming"
          @click="confirmEmailCode"
        >
          确认验证码
        </el-button>
      </template>
    </el-dialog>

    <BatchRecoveryEmailDialog ref="batchRecoveryEmailRef" @completed="onBatchRecoveryEmailCompleted" />

    <el-dialog v-model="batchProfile.visible" :title="batchProfile.title" width="560px">
      <el-form label-position="top">
        <template v-if="batchProfile.mode === 'nickname'">
          <el-alert
            title="每行一个昵称模板，按顺序分配给已选账号；用完后从头轮询。"
            type="warning"
            :closable="false"
            show-icon
            class="mb-3"
          />
          <el-form-item label="昵称模板（换行分隔）">
            <el-input
              v-model="batchProfile.value"
              type="textarea"
              :rows="6"
              placeholder="例如：{diqu}歌神{geshou}"
            />
          </el-form-item>
          <el-checkbox v-model="batchProfile.appendPhoneLast4WhenDuplicate">
            昵称重复时追加手机号后 4 位
          </el-checkbox>
          <div v-if="textVariableText" class="field-help">可用文本变量：{{ textVariableText }}</div>
        </template>
        <template v-else-if="batchProfile.mode === 'bio'">
          <el-form-item label="Bio（简介）">
            <el-input v-model="batchProfile.value" type="textarea" :rows="4" placeholder="支持 {time}、文本字典变量以及 \\n 或 /n 换行；留空可清空" />
          </el-form-item>
          <div v-if="textVariableText" class="field-help">可用文本变量：{{ textVariableText }}</div>
        </template>
        <template v-else-if="batchProfile.mode === 'username'">
          <el-alert
            title="用户名模板不要带开头的 @，批量执行时建议使用文本字典提供不同值。"
            type="warning"
            :closable="false"
            show-icon
            class="mb-3"
          />
          <el-form-item label="用户名模板">
            <el-input v-model="batchProfile.value" placeholder="例如：tg_{city}_{time}" />
          </el-form-item>
          <div class="field-help">支持变量：{time}、{index}、{id}、{phone}、{last4}</div>
          <div v-if="textVariableText" class="field-help">可用文本变量：{{ textVariableText }}</div>
        </template>
        <template v-else>
          <el-alert
            title="支持固定上传单个头像，或选择图片字典变量按字典读取方式为每个账号取图。"
            type="warning"
            :closable="false"
            show-icon
            class="mb-3"
          />
          <el-form-item label="头像来源">
            <el-radio-group v-model="batchProfile.avatarSource">
              <el-radio-button label="fixed">固定上传</el-radio-button>
              <el-radio-button label="dictionary">图片字典变量</el-radio-button>
            </el-radio-group>
          </el-form-item>
          <el-form-item v-if="batchProfile.avatarSource === 'fixed'" label="头像图片">
            <el-upload
              v-model:file-list="batchProfile.avatarFiles"
              :auto-upload="false"
              :limit="1"
              accept="image/*"
              :on-change="onBatchAvatarChange"
              :on-remove="onBatchAvatarRemove"
            >
              <el-button :icon="Picture">选择头像图片</el-button>
            </el-upload>
          </el-form-item>
          <el-form-item v-else label="图片字典">
            <el-select v-model="batchProfile.dictionaryName" class="full" placeholder="请选择图片字典">
              <el-option v-for="item in imageDictionaries" :key="item.name" :label="`${item.displayName}（{${item.name}}）`" :value="item.name" />
            </el-select>
          </el-form-item>
        </template>
        <div class="muted">账号数量：{{ selectedIds.length }}</div>
      </el-form>
      <template #footer>
        <el-button @click="batchProfile.visible = false">关闭</el-button>
        <el-button type="primary" :loading="batchProfile.running" @click="submitBatchProfile">开始修改</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="listDialog.visible"
      :title="listDialog.title"
      :width="listDialogWidth"
      class="account-list-dialog"
    >
      <el-skeleton v-if="listDialog.loading" :rows="6" animated />
      <template v-else>
        <el-table v-if="listDialog.type === 'memberships'" :data="listDialog.memberships" stripe>
          <el-table-column prop="title" label="名称" min-width="180" />
          <el-table-column label="用户名" min-width="140">
            <template #default="{ row }">{{ row.username ? `@${row.username}` : '-' }}</template>
          </el-table-column>
          <el-table-column label="角色" width="110">
            <template #default="{ row }">
              <el-tag :type="row.isCreator ? 'success' : row.isAdmin ? 'primary' : 'info'" size="small">
                {{ row.isCreator ? '创建者' : row.isAdmin ? '管理员' : '成员' }}
              </el-tag>
            </template>
          </el-table-column>
          <el-table-column prop="categoryName" label="分类" min-width="120">
            <template #default="{ row }">{{ row.categoryName || '未分类' }}</template>
          </el-table-column>
          <el-table-column prop="memberCount" label="成员数" width="100" />
          <el-table-column label="最后同步" min-width="170">
            <template #default="{ row }">{{ formatTime(row.syncedAt) }}</template>
          </el-table-column>
        </el-table>

        <el-table v-else-if="listDialog.type === 'devices'" :data="listDialog.devices" stripe fit class="devices-table">
          <el-table-column label="当前" width="64" align="center">
            <template #default="{ row }"><el-tag :type="row.current ? 'success' : 'info'" size="small">{{ row.current ? '是' : '否' }}</el-tag></template>
          </el-table-column>
          <el-table-column label="应用 / API / 设备" min-width="340">
            <template #default="{ row }">
              <div class="device-title">{{ row.appName || 'UnknownApp' }} - {{ row.deviceDisplayName || row.deviceModel || 'UnknownDevice' }}</div>
              <div class="device-api-line">
                <el-tag size="small" effect="plain">ApiId={{ row.apiId }}</el-tag>
                <el-tag size="small" type="info" effect="plain">{{ row.apiFamily || '未知 API' }}</el-tag>
              </div>
              <div class="cell-sub device-meta">应用/平台：{{ row.apiDisplayName || [row.appName, row.platform].filter(Boolean).join(' / ') || '-' }}</div>
              <div class="cell-sub device-meta">版本：{{ row.appVersion || '-' }}</div>
              <el-tooltip v-if="row.apiDescription" :content="row.apiDescription" placement="top" effect="light">
                <el-tag class="device-help-tag" size="small" type="warning" effect="plain">说明</el-tag>
              </el-tooltip>
            </template>
          </el-table-column>
          <el-table-column label="设备画像 / 系统" min-width="220">
            <template #default="{ row }">
              <div class="device-text">{{ row.deviceDisplayName || row.deviceModel || '-' }}</div>
              <div class="cell-sub device-meta">{{ [row.platform, row.systemVersion].filter(Boolean).join(' ') || '-' }}</div>
            </template>
          </el-table-column>
          <el-table-column label="IP/地区" min-width="220">
            <template #default="{ row }">
              <div class="device-text">{{ row.ip || '-' }}</div>
              <div class="cell-sub device-meta">{{ [row.country, row.region].filter(Boolean).join(' ') }}</div>
            </template>
          </el-table-column>
          <el-table-column label="最近活跃" min-width="170">
            <template #default="{ row }">{{ formatTime(row.lastActiveAtUtc, '-') }}</template>
          </el-table-column>
          <el-table-column label="操作" width="74" align="center">
            <template #default="{ row }">
              <el-button link type="danger" :disabled="row.current" @click="kickDevice(row)">踢出</el-button>
            </template>
          </el-table-column>
        </el-table>

        <el-scrollbar v-else height="420px">
          <el-empty v-if="listDialog.messages.length === 0" description="暂无系统通知" />
          <div v-for="message in listDialog.messages" :key="message.id" class="message-item">
            <div class="cell-sub">{{ formatTime(message.dateUtc, '-') }}</div>
            <div class="message-text">{{ message.text }}</div>
          </div>
        </el-scrollbar>
      </template>
      <template #footer>
        <el-button v-if="listDialog.type === 'devices'" :loading="listDialog.loading" @click="kickAllDevicesForDialog">踢出所有其他设备</el-button>
        <el-button @click="listDialog.visible = false">关闭</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="resultDialog.visible" :title="resultDialog.title" width="700px">
      <div class="result-summary">{{ resultDialog.summary }}</div>
      <el-table v-if="resultDialog.items.length" :data="resultDialog.items" stripe max-height="420">
        <el-table-column prop="phone" label="账号" min-width="150" />
        <el-table-column label="结果" width="90">
          <template #default="{ row }">
            <el-tag :type="row.success ? 'success' : 'danger'" size="small">{{ row.success ? '成功' : '失败' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="summary" label="摘要" min-width="120" />
        <el-table-column prop="error" label="原因" min-width="220" />
      </el-table>
      <template #footer>
        <el-button type="primary" @click="resultDialog.visible = false">知道了</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="exportDialog.visible" title="选择导出格式" width="520px">
      <p>将导出 {{ exportDialog.ids.length }} 个账号（{{ exportDialog.scopeLabel }}）。</p>
      <el-alert title="导出时会自动为每个账号生成一份独立新 session，避免和面板当前在线 session 冲突。" type="info" :closable="false" show-icon />
      <div class="export-options">
        <el-card shadow="never">
          <h4>Telethon（默认）</h4>
          <p class="muted">每个账号导出独立 .json + .session (+2fa.txt)，适合现有批量导入流程。</p>
          <el-button type="primary" @click="exportAccounts('telethon')">导出 Telethon</el-button>
        </el-card>
        <el-card shadow="never">
          <h4>Tdata</h4>
          <p class="muted">每个账号额外导出独立 tdata/，并同时保留 .json + .session (+2fa.txt)。</p>
          <el-button @click="exportAccounts('tdata')">导出 Tdata</el-button>
        </el-card>
      </div>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref, watch } from 'vue'
import type { Component } from 'vue'
import {
  ArrowDown,
  ChatDotRound,
  CollectionTag,
  Connection,
  Delete,
  Edit,
  Hide,
  InfoFilled,
  Lock,
  Message,
  Monitor,
  MoreFilled,
  Picture,
  Position,
  Promotion,
  Refresh,
  Search,
  Select,
  Switch,
  SwitchButton,
  UserFilled,
  View,
} from '@element-plus/icons-vue'
import type { TableInstance, UploadFile } from 'element-plus'
import { ElMessage, ElMessageBox } from 'element-plus'
import { panelApi } from '@/api/panel'
import BatchChatMembershipDialog from '@/components/BatchChatMembershipDialog.vue'
import BatchRecoveryEmailDialog from '@/components/BatchRecoveryEmailDialog.vue'
import ColumnVisibilityMenu from '@/components/ColumnVisibilityMenu.vue'
import { confirmChatMembershipRisk } from '@/utils/riskWarning'
import type {
  AccountBatchOperationResult,
  AccountCategory,
  AccountChatMembership,
  AccountDetail,
  AccountListItem,
  AccountOperationItem,
  AccountProxyStrategy,
  BatchTask,
  DataDictionary,
  OutboundProxy,
  ProxyKind,
  WarpRuntimeStatus,
  TelegramStatus,
  TelegramAuthorization,
  TelegramSystemMessage,
} from '@/api/types'
import { formatTime } from '@/utils/format'
import { isInconclusiveTelegramStatus, isTransientTelegramStatus } from '@/utils/telegramStatus'
import { accountCategoryTagStyle } from '@/utils/categoryStyle'
import { usePersistentColumnVisibility, type ColumnVisibilityOption } from '@/utils/columnVisibility'
import { useMediaQuery } from '@/utils/useMediaQuery'

type Row = AccountListItem & { busy?: boolean }
type SelectionMode = 'select' | 'invert' | 'clear'

const loading = ref(false)
const rows = ref<Row[]>([])
const categories = ref<AccountCategory[]>([])
const dictionaries = ref<DataDictionary[]>([])
const proxies = ref<OutboundProxy[]>([])
const warpStatus = ref<WarpRuntimeStatus | null>(null)
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const tableRef = ref<TableInstance>()
const batchChatMembershipRef = ref<InstanceType<typeof BatchChatMembershipDialog>>()
const batchRecoveryEmailRef = ref<InstanceType<typeof BatchRecoveryEmailDialog>>()
const selectedRows = ref<Row[]>([])
// 状态检测独立于列表加载状态：只标记正在检测的账号，避免整页出现 loading 遮罩。
const refreshingStatusIds = reactive(new Set<number>())
// 每个刷新队列拥有独立令牌，避免旧队列结束时误清理新队列刚接手的账号。
const statusRefreshOwners = new Map<number, symbol>()
// 防止较晚返回的旧列表请求覆盖刚完成的逐行状态响应。
const latestStatusById = new Map<number, TelegramStatus>()
const proxyEgressCheckingIds = reactive(new Set<number>())
const selectionMode = ref<SelectionMode>('select')
const isCompactList = useMediaQuery('(max-width: 640px)')
const filters = reactive({
  categoryId: null as number | null,
  search: '',
  onlyWaste: false,
})

const accountColumns: ColumnVisibilityOption[] = [
  { key: 'phone', label: '手机号' },
  { key: 'proxy', label: '代理' },
  { key: 'nickname', label: '昵称' },
  { key: 'username', label: '用户名' },
  { key: 'remark', label: '备注' },
  { key: 'userId', label: '用户ID' },
  { key: 'category', label: '分类' },
  { key: 'chatCounts', label: '频道/群组' },
  { key: 'telegramStatus', label: 'Telegram 状态' },
  { key: 'registrationAt', label: '注册时间' },
  { key: 'lastSyncAt', label: '最后数据同步' },
]

const { visibleColumnKeys, isColumnVisible, resetColumns, showAllColumns } = usePersistentColumnVisibility(
  'telegram-panel.accounts.columns',
  accountColumns,
)

let filterTimer: number | undefined

const selectedIds = computed(() => selectedRows.value.map((x) => x.id))
const statusRefreshCount = computed(() => refreshingStatusIds.size)
const selectedStatusRefreshing = computed(() => selectedIds.value.some((id) => refreshingStatusIds.has(id)))
const selectedMutationDisabled = computed(() => selectedIds.value.length === 0 || selectedStatusRefreshing.value)
const imageDictionaries = computed(() =>
  dictionaries.value
    .filter((x) => x.isEnabled && x.type === 'image' && x.enabledItemCount > 0)
    .sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN')),
)
const textDictionaries = computed(() =>
  dictionaries.value
    .filter((x) => x.isEnabled && x.type === 'text' && x.enabledItemCount > 0)
    .sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN')),
)
const textVariableText = computed(() => textDictionaries.value.map((x) => `{${x.name}}`).join('、'))
const selectionText = computed(() => {
  if (selectionMode.value === 'invert') return '反选本页'
  if (selectionMode.value === 'clear') return '清空选择'
  return '全选本页'
})
const selectionIcon = computed<Component>(() => {
  if (selectionMode.value === 'invert') return Switch
  if (selectionMode.value === 'clear') return Delete
  return Select
})

const details = reactive({
  visible: false,
  loading: false,
  saving: false,
  showPassword: false,
  account: null as AccountDetail | null,
  form: {
    remark: '',
    twoFactorPassword: '',
  },
})

const profile = reactive({
  visible: false,
  saving: false,
  row: null as Row | null,
  avatarFile: null as File | null,
  form: {
    editNickname: false,
    nickname: '',
    editBio: false,
    bio: '',
    editUsername: false,
    username: '',
    editAvatar: false,
  },
})

const categoryDialog = reactive({
  visible: false,
  saving: false,
  categoryId: null as number | null,
})

const proxyDialog = reactive({
  visible: false,
  running: false,
  title: '切换代理',
  accountIds: [] as number[],
  strategy: '' as AccountProxyStrategy | '',
  proxyId: null as number | null,
  expectedProxyId: null as number | null,
})
let accountProxyOperationToken = 0
const warpAvailable = computed(() => Boolean(
  warpStatus.value?.platformSupported
  && warpStatus.value.enabled
  && warpStatus.value.dockerAvailable,
))

const twoFactor = reactive({
  visible: false,
  running: false,
  accountIds: [] as number[],
  form: {
    currentPassword: '',
    newPassword: '',
    confirmPassword: '',
    hint: '',
    useStoredPasswords: true,
    saveNewPasswordToDb: true,
  },
})

const emailDialog = reactive({
  visible: false,
  sending: false,
  confirming: false,
  kind: 'recovery' as 'recovery' | 'login',
  row: null as Row | null,
  title: '',
  statusText: '',
  currentPassword: '',
  email: '',
  code: '',
  hasPendingRecoveryEmail: false,
  canOpenTwoFactor: false,
})

const batchProfile = reactive({
  visible: false,
  running: false,
  mode: 'nickname' as 'nickname' | 'bio' | 'username' | 'avatar',
  title: '',
  value: '',
  appendPhoneLast4WhenDuplicate: true,
  avatarSource: 'fixed' as 'fixed' | 'dictionary',
  avatarFile: null as File | null,
  avatarFiles: [] as UploadFile[],
  dictionaryName: '',
})

const listDialog = reactive({
  visible: false,
  loading: false,
  title: '',
  type: 'memberships' as 'memberships' | 'devices' | 'messages',
  accountId: 0,
  memberships: [] as AccountChatMembership[],
  devices: [] as TelegramAuthorization[],
  messages: [] as TelegramSystemMessage[],
})
const listDialogWidth = computed(() => {
  if (listDialog.type === 'devices') return 'min(1080px, calc(100vw - 32px))'
  if (listDialog.type === 'memberships') return 'min(920px, calc(100vw - 32px))'
  return 'min(760px, calc(100vw - 32px))'
})

const resultDialog = reactive({
  visible: false,
  title: '',
  summary: '',
  items: [] as AccountOperationItem[],
})

const exportDialog = reactive({
  visible: false,
  ids: [] as number[],
  scopeLabel: '',
})

function openBatchRecoveryEmail() {
  if (!ensureSelected()) return
  batchRecoveryEmailRef.value?.open(selectedIds.value)
}

function onBatchRecoveryEmailCompleted(result: AccountBatchOperationResult) {
  showBatchResult('批量换绑邮箱完成', result)
  load()
}

async function load() {
  loading.value = true
  try {
    const data = await panelApi.accounts({
      page: page.value,
      pageSize: pageSize.value,
      categoryId: filters.categoryId,
      search: filters.search,
      onlyWaste: filters.onlyWaste,
    })
    // 列表重新加载时保留后台检测标记，并合并更新较晚的逐行状态响应。
    rows.value = data.items.map(mergeLatestStatus)
    total.value = data.total
    selectedRows.value = []
    tableRef.value?.clearSelection()
    selectionMode.value = 'select'
  } finally {
    loading.value = false
  }
}

async function reload() {
  selectedRows.value = []
  tableRef.value?.clearSelection()
  await load()
}

async function loadCategories() {
  categories.value = await panelApi.accountCategories()
}

async function loadDictionaries() {
  dictionaries.value = await panelApi.dictionaries()
}

async function loadProxies() {
  proxies.value = await panelApi.proxies()
}

async function loadWarpStatus() {
  try {
    warpStatus.value = await panelApi.warpStatus()
  } catch {
    warpStatus.value = null
  }
}

function onSelectionChange(selection: Row[]) {
  selectedRows.value = selection
}

function isStatusRefreshing(accountId: number) {
  return refreshingStatusIds.has(accountId)
}

function statusTime(value?: string | null) {
  const timestamp = value ? Date.parse(value) : Number.NaN
  return Number.isFinite(timestamp) ? timestamp : 0
}

function mergeLatestStatus(item: Row): Row {
  const cached = latestStatusById.get(item.id)
  if (!cached || statusTime(item.telegramStatusCheckedAtUtc) >= statusTime(cached.checkedAtUtc)) {
    if (cached) latestStatusById.delete(item.id)
    return { ...item, busy: refreshingStatusIds.has(item.id) }
  }

  return {
    ...item,
    telegramStatusOk: cached.ok,
    telegramStatusSummary: cached.summary,
    telegramStatusDetails: cached.details,
    telegramStatusCheckedAtUtc: cached.checkedAtUtc,
    busy: refreshingStatusIds.has(item.id),
  }
}

function ensureAccountsIdle(accountIds: number[], action: string) {
  const count = [...new Set(accountIds)].filter((id) => refreshingStatusIds.has(id)).length
  if (count === 0) return true
  ElMessage.warning(`有 ${count} 个账号正在刷新状态，请完成后再${action}`)
  return false
}

function reserveStatusRefreshing(accountId: number, owner: symbol) {
  if (statusRefreshOwners.has(accountId)) return false
  statusRefreshOwners.set(accountId, owner)
  refreshingStatusIds.add(accountId)

  const row = rows.value.find((item) => item.id === accountId)
  if (row) row.busy = true
  return true
}

function releaseStatusRefreshing(accountId: number, owner: symbol) {
  if (statusRefreshOwners.get(accountId) !== owner) return
  statusRefreshOwners.delete(accountId)
  refreshingStatusIds.delete(accountId)

  const row = rows.value.find((item) => item.id === accountId)
  if (row) row.busy = false
}

function cycleSelection() {
  if (!tableRef.value) return
  if (selectionMode.value === 'select') {
    rows.value.forEach((row) => tableRef.value?.toggleRowSelection(row, true))
    selectionMode.value = 'invert'
    return
  }
  if (selectionMode.value === 'invert') {
    rows.value.forEach((row) => tableRef.value?.toggleRowSelection(row, !selectedIds.value.includes(row.id)))
    selectionMode.value = 'clear'
    return
  }
  tableRef.value.clearSelection()
  selectionMode.value = 'select'
}

function buildStatusTitle(row: Row) {
  if (isStatusRefreshing(row.id)) return '正在后台刷新 Telegram 状态，请稍候…'
  if (!row.isActive) {
    const lastStatus = row.telegramStatusSummary ? `；上次 Telegram 检测：${row.telegramStatusSummary}` : ''
    return `面板已停用：该账号不会参与任务和批量操作${lastStatus}`
  }
  const detailsText = row.telegramStatusDetails || row.telegramStatusSummary || ''
  const checkedAt = formatTime(row.telegramStatusCheckedAtUtc, '-')
  if (!detailsText) return '尚未检测 Telegram 状态'
  return `${detailsText}（检测时间：${checkedAt}）`
}

function telegramStatusText(row: Row) {
  if (isStatusRefreshing(row.id)) return '刷新中…'
  if (!row.isActive) return '停用'
  if (!row.telegramStatusSummary) return '未检测'
  if (!row.telegramStatusOk && isTransientTelegramStatus(row.telegramStatusSummary)) return '连接异常'
  if (!row.telegramStatusOk && isInconclusiveTelegramStatus(row.telegramStatusSummary)) return '检测异常'
  return row.telegramStatusOk ? row.telegramStatusSummary : '失效'
}

function telegramStatusTagType(row: Row) {
  if (isStatusRefreshing(row.id)) return 'warning'
  if (!row.isActive) return 'info'
  if (!row.telegramStatusSummary) return 'info'
  if (!row.telegramStatusOk && isInconclusiveTelegramStatus(row.telegramStatusSummary)) return 'warning'
  return row.telegramStatusOk ? 'success' : 'danger'
}

function proxyKindLabel(kind?: ProxyKind | null) {
  if (kind === 'wireguard_warp') return 'WG WARP'
  if (kind === 'warp') return 'WARP'
  if (kind === 'resin') return 'Resin'
  return '代理'
}

function proxyKindTagType(kind?: ProxyKind | null) {
  if (kind === 'warp') return 'success'
  if (kind === 'wireguard_warp') return 'primary'
  if (kind === 'resin') return 'warning'
  return 'info'
}

async function openDetails(row: Row) {
  details.visible = true
  details.loading = true
  details.account = null
  try {
    const account = await panelApi.account(row.id)
    details.account = account
    details.form.remark = account.remark || ''
    details.form.twoFactorPassword = account.twoFactorPassword || ''
  } finally {
    details.loading = false
  }
}

async function saveDetails() {
  if (!details.account) return
  if (!ensureAccountsIdle([details.account.id], '保存账号详情')) return
  details.saving = true
  try {
    await panelApi.updateAccount(details.account.id, {
      remark: details.form.remark,
      twoFactorPassword: details.form.twoFactorPassword,
      categoryId: details.account.categoryId ?? null,
    })
    ElMessage.success('账号详情已保存')
    details.visible = false
    await load()
  } finally {
    details.saving = false
  }
}

function openProfile(row: Row) {
  if (!ensureAccountsIdle([row.id], '编辑用户资料')) return
  profile.row = row
  profile.avatarFile = null
  profile.form.editNickname = false
  profile.form.nickname = row.nickname || ''
  profile.form.editBio = false
  profile.form.bio = ''
  profile.form.editUsername = false
  profile.form.username = row.username || ''
  profile.form.editAvatar = false
  profile.visible = true
}

function onAvatarChange(file: UploadFile) {
  profile.avatarFile = file.raw || null
}

function onAvatarRemove() {
  profile.avatarFile = null
}

function onBatchAvatarChange(file: UploadFile) {
  batchProfile.avatarFile = file.raw || null
}

function onBatchAvatarRemove() {
  batchProfile.avatarFile = null
}

async function saveProfile() {
  if (!profile.row) return
  if (!ensureAccountsIdle([profile.row.id], '保存用户资料')) return
  const form = profile.form
  if (!form.editNickname && !form.editBio && !form.editUsername && !form.editAvatar) {
    ElMessage.warning('请选择要修改的资料项')
    return
  }
  if (form.editNickname && !form.nickname.trim()) {
    ElMessage.warning('请填写昵称')
    return
  }
  if (form.editAvatar && !profile.avatarFile) {
    ElMessage.warning('请先选择头像图片')
    return
  }

  await ElMessageBox.confirm('将修改该账号 Telegram 资料（昵称/Bio/用户名/头像）。是否继续？', '确认保存', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })
  const safeIds = await confirmChatMembershipRisk([profile.row.id], '风控警告')
  if (!safeIds) return

  const data = new FormData()
  data.append('editNickname', String(form.editNickname))
  data.append('nickname', form.nickname)
  data.append('editBio', String(form.editBio))
  data.append('bio', form.bio)
  data.append('editUsername', String(form.editUsername))
  data.append('username', form.username)
  data.append('editAvatar', String(form.editAvatar))
  if (profile.avatarFile) data.append('avatar', profile.avatarFile)

  profile.saving = true
  try {
    await panelApi.updateAccountProfile(profile.row.id, data)
    ElMessage.success('保存成功')
    profile.visible = false
    await load()
  } finally {
    profile.saving = false
  }
}

function getStatusRefreshError(error: unknown) {
  if (error instanceof Error && error.message) return error.message
  if (typeof error === 'string' && error.trim()) return error
  return '请求失败，请稍后重试'
}

/**
 * 先登记整批待检测账号，再按顺序执行状态检测，并在每个请求完成后立即写回当前列表行。
 * 这里刻意不设置全局 loading，避免列表被遮罩；Set 同时承担队列占用标记，防止重复请求。
 */
async function refreshStatusRows(accountIds: number[], probeCreateChannel: boolean): Promise<AccountBatchOperationResult> {
  const requestedIds = [...new Set(accountIds)].filter((id) => id > 0)
  const items: AccountOperationItem[] = []
  const owner = Symbol('account-status-refresh')
  const reservedIds: number[] = []
  const skippedIds: number[] = []

  // 先同步占用整批账号。这样在第一个请求等待网络时，后续账号也不会被另一条刷新操作重复占用。
  requestedIds.forEach((id) => {
    if (reserveStatusRefreshing(id, owner)) reservedIds.push(id)
    else skippedIds.push(id)
  })
  skippedIds.forEach((accountId) => {
    const row = rows.value.find((item) => item.id === accountId)
    items.push({
      accountId,
      phone: row?.displayPhone ?? null,
      success: false,
      summary: '刷新跳过',
      error: '该账号正在刷新',
    })
  })

  try {
    for (const accountId of reservedIds) {
      const initialRow = rows.value.find((item) => item.id === accountId)
      try {
        const status = await panelApi.refreshTelegramStatusWithProbe(accountId, probeCreateChannel)
        latestStatusById.set(accountId, status)
        const row = rows.value.find((item) => item.id === accountId)
        if (row) {
          row.telegramStatusOk = status.ok
          row.telegramStatusSummary = status.summary
          row.telegramStatusDetails = status.details
          row.telegramStatusCheckedAtUtc = status.checkedAtUtc
        }
        items.push({
          accountId,
          phone: row?.displayPhone ?? initialRow?.displayPhone ?? null,
          success: status.ok,
          summary: status.summary,
          error: status.ok ? null : status.details || null,
        })
      } catch (error) {
        const row = rows.value.find((item) => item.id === accountId)
        items.push({
          accountId,
          phone: row?.displayPhone ?? initialRow?.displayPhone ?? null,
          success: false,
          summary: '刷新失败',
          error: getStatusRefreshError(error),
        })
      } finally {
        releaseStatusRefreshing(accountId, owner)
        // 让当前账号的最新状态/加载图标先渲染，再开始下一个账号。
        await nextTick()
      }
    }
  } finally {
    // 即使出现未预期的运行时异常，也不能让剩余账号永久停留在“刷新中”。
    reservedIds.forEach((id) => releaseStatusRefreshing(id, owner))
  }

  return {
    success: items.filter((item) => item.success).length,
    failed: items.filter((item) => !item.success).length,
    items,
  }
}

async function refreshStatus(row: Row) {
  if (isStatusRefreshing(row.id)) return
  const probe = await chooseProbe(
    '刷新 Telegram 状态',
    '是否进行深度探测（将创建并删除一个测试频道，用于判断【创建频道接口是否被冻结】）？',
  )
  if (probe === null) return
  try {
    const result = await refreshStatusRows([row.id], probe)
    const item = result.items[0]
    if (!item) return
    const message = `账号 ${row.displayPhone} 状态：${item.summary}`
    if (item.success) ElMessage.success(message)
    else ElMessage.warning(`${message}${item.error ? `（${item.error}）` : ''}`)
  } catch (error) {
    // 兜底处理渲染/运行时异常，避免单行一直停留在“刷新中”状态。
    ElMessage.error(`刷新失败：${getStatusRefreshError(error)}`)
  }
}

async function batchRefreshStatus() {
  if (!ensureSelected()) return
  const selected = [...new Set(selectedIds.value)].filter((id) => id > 0)
  const ids = selected.filter((id) => !isStatusRefreshing(id))
  if (ids.length === 0) {
    ElMessage.info(selected.length > 0 ? '所选账号正在刷新，请稍候' : '没有可刷新的账号')
    return
  }
  const probe = await chooseProbe(
    '刷新已选 Telegram 状态',
    `将刷新已选 ${ids.length} 个账号的 Telegram 状态。\n\n是否进行深度探测（将对每个账号创建并删除一个测试频道，用于判断【创建频道接口是否被冻结】）？`,
  )
  if (probe === null) return

  // 后台顺序执行：当前函数立即返回，列表仍可操作，状态按账号逐个更新。
  ElMessage.info(`已开始后台刷新 ${ids.length} 个账号，列表将逐个更新状态`)
  void refreshStatusRows(ids, probe)
    .then((result) => showBatchResult('批量刷新完成', result))
    .catch((error) => ElMessage.error(`批量刷新异常：${getStatusRefreshError(error)}`))
}

async function chooseProbe(title: string, message: string) {
  try {
    await ElMessageBox.confirm(message, title, {
      type: 'warning',
      confirmButtonText: '深度探测',
      cancelButtonText: '普通刷新',
      distinguishCancelAndClose: true,
    })
    return true
  } catch (action) {
    return action === 'cancel' ? false : null
  }
}

async function toggleActive(row: Row) {
  const next = !row.isActive
  await panelApi.setAccountActive(row.id, next)
  row.isActive = next
  ElMessage.success(next ? '账号已启用' : '账号已停用')
}

async function deleteOne(row: Row) {
  if (!ensureAccountsIdle([row.id], '删除账号')) return
  await ElMessageBox.confirm(`确定要删除账号 ${row.displayPhone} 吗？此操作不可撤销！`, '确认删除', {
    type: 'warning',
    confirmButtonText: '删除',
    cancelButtonText: '取消',
  })
  await panelApi.deleteAccount(row.id)
  ElMessage.success('账号已删除')
  await load()
}

async function deleteSelected() {
  if (!ensureSelected()) return
  if (!ensureAccountsIdle(selectedIds.value, '删除账号')) return
  await ElMessageBox.confirm(
    `确定要删除已选账号（${selectedIds.value.length} 个）吗？将同时清理 sessions 文件，且不可恢复。`,
    '确认删除',
    { type: 'warning', confirmButtonText: '删除', cancelButtonText: '取消' },
  )
  loading.value = true
  try {
    const result = await panelApi.batchDeleteAccounts(selectedIds.value)
    showBatchResult('删除完成', result)
    await load()
  } finally {
    loading.value = false
  }
}

async function batchKickDevices() {
  if (!ensureSelected()) return
  if (!ensureAccountsIdle(selectedIds.value, '踢出其他设备')) return
  await ElMessageBox.confirm(`将对 ${selectedIds.value.length} 个账号执行【踢出所有其他设备】（会保留面板当前会话）。是否继续？`, '确认踢出', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })
  loading.value = true
  try {
    const result = await panelApi.batchKickAllOtherDevices(selectedIds.value)
    showBatchResult('批量踢出完成', result)
  } finally {
    loading.value = false
  }
}

async function cleanupWaste(scope: 'selected' | 'filtered' | 'all') {
  if (scope === 'selected' && !ensureSelected()) return
  if (scope === 'selected' && !ensureAccountsIdle(selectedIds.value, '清理废号')) return
  if (scope !== 'selected' && statusRefreshCount.value > 0) {
    ElMessage.warning('后台状态刷新完成后才能清理筛选或全部账号')
    return
  }
  if (scope === 'filtered' && rows.value.length === 0) {
    ElMessage.info('当前筛选条件下没有可清理的废号')
    return
  }

  const countText = scope === 'selected' ? `已选 ${selectedIds.value.length} 个账号` : scope === 'filtered' ? '当前筛选结果' : `系统全部账号（共 ${total.value} 个）`
  const probe = await chooseProbe(
    scope === 'all' ? '清理所有废号' : scope === 'filtered' ? '清理筛选废号' : '清理已选废号',
    `将对${countText}执行 Telegram 状态检测，并删除判定为废号的账号与 session 文件。\n\n是否进行深度探测？`,
  )
  if (probe === null) return

  if (scope === 'all' && probe) {
    await ElMessageBox.confirm('你选择了【深度探测】并且范围是【全部账号】。这会对每个账号创建并删除测试频道，属于高频敏感操作。确定继续吗？', '二次确认（高风险）', {
      type: 'warning',
      confirmButtonText: '继续',
      cancelButtonText: '取消',
    })
  }

  loading.value = true
  try {
    const result = await panelApi.cleanupWasteAccounts({
      scope,
      accountIds: scope === 'selected' ? selectedIds.value : undefined,
      categoryId: filters.categoryId,
      search: filters.search,
      probeCreateChannel: probe,
    })
    resultDialog.title = '清理完成'
    resultDialog.summary = `删除 ${result.deleted}，跳过 ${result.skipped}，失败 ${result.failed}`
    resultDialog.items = result.items
    resultDialog.visible = true
    await load()
  } finally {
    loading.value = false
  }
}

function openChatDialog(operation: 'join' | 'leave', accountIds: number[]) {
  if (!ensureAccountsIdle(accountIds, operation === 'join' ? '加群/订阅' : '退群/退订')) return
  batchChatMembershipRef.value?.open(operation, accountIds)
}

async function onChatMembershipCompleted(title: string, task: BatchTask) {
  ElMessage.success(`${title}：#${task.id}，请到任务中心查看进度`)
  await load()
}

function openBatchCategory() {
  if (!ensureSelected()) return
  categoryDialog.categoryId = null
  categoryDialog.visible = true
}

async function saveBatchCategory() {
  categoryDialog.saving = true
  try {
    await panelApi.batchSetAccountCategory(selectedIds.value, categoryDialog.categoryId)
    ElMessage.success(`分类已更新：${selectedIds.value.length} 个账号`)
    categoryDialog.visible = false
    await load()
  } finally {
    categoryDialog.saving = false
  }
}

function openAccountProxy(accountIds: number[], row?: Row) {
  if (proxyDialog.running) return
  accountProxyOperationToken += 1
  proxyDialog.accountIds = [...accountIds]
  proxyDialog.title = accountIds.length > 1 ? `批量切换代理（${accountIds.length} 个账号）` : `切换代理 - ${row?.displayPhone || ''}`
  proxyDialog.expectedProxyId = row?.proxy?.id ?? 0
  proxyDialog.strategy = row
    ? row.proxy ? 'existing' : row.useGlobalProxy ? 'global' : 'direct'
    : ''
  proxyDialog.proxyId = row?.proxy?.id ?? null
  proxyDialog.visible = true
}

function beforeAccountProxyDialogClose(done: () => void) {
  if (!proxyDialog.running) done()
}

async function saveAccountProxy() {
  if (proxyDialog.running) return
  const accountIds = [...proxyDialog.accountIds]
  const strategy = proxyDialog.strategy
  const proxyId = proxyDialog.proxyId
  const expectedProxyId = proxyDialog.expectedProxyId

  if (!strategy) {
    ElMessage.warning('请先明确选择本次账号切换使用的代理方式')
    return
  }
  if (strategy === 'existing' && !proxyId) {
    ElMessage.warning('请选择要绑定的代理')
    return
  }
  if (strategy === 'warp_per_account' && !warpAvailable.value) {
    ElMessage.warning(warpStatus.value?.error || '当前环境无法创建 WARP')
    return
  }
  if (strategy === 'warp_per_account' && accountIds.length > 10) {
    ElMessage.warning('逐账号创建 WARP 单次最多处理 10 个账号')
    return
  }

  const operationToken = ++accountProxyOperationToken
  proxyDialog.running = true
  try {
    const payload = {
      strategy,
      proxyId: strategy === 'existing' ? proxyId : null,
    }
    if (accountIds.length === 1) {
      const result = await panelApi.setAccountProxy(accountIds[0], {
        ...payload,
        expectedProxyId,
      })
      if (operationToken !== accountProxyOperationToken) return
      const item = result.items[0]
      if (!item?.success) {
        ElMessage.error(item?.error || item?.summary || '代理切换失败')
        return
      }
      ElMessage.success(item.summary || '代理已切换')
      proxyDialog.visible = false
      await Promise.allSettled([load(), loadProxies()])
      return
    }

    const result = await panelApi.batchSetAccountProxy(accountIds, payload)
    if (operationToken !== accountProxyOperationToken) return
    proxyDialog.visible = false
    showBatchResult('批量切换代理完成', result)
    await Promise.allSettled([load(), loadProxies()])
  } finally {
    if (operationToken === accountProxyOperationToken) proxyDialog.running = false
  }
}

async function checkAccountProxyEgress(row: Row) {
  if (!row.proxy || proxyEgressCheckingIds.has(row.id)) return
  proxyEgressCheckingIds.add(row.id)
  try {
    const result = await panelApi.accountProxyEgress(row.id)
    if (result.success) {
      const location = [result.country, result.city].filter(Boolean).join(' / ')
      ElMessage.success(`代理出口：${result.ip || '未知'}${location ? ` · ${location}` : ''} · ${result.latencyMs ?? '-'} ms`)
    } else {
      ElMessage.error(result.error || '代理出口检测失败')
    }
    await load()
  } finally {
    proxyEgressCheckingIds.delete(row.id)
  }
}

async function openTwoFactor(accountIds: number[]) {
  twoFactor.accountIds = accountIds
  twoFactor.form.currentPassword = ''
  twoFactor.form.newPassword = ''
  twoFactor.form.confirmPassword = ''
  twoFactor.form.hint = ''
  twoFactor.form.useStoredPasswords = accountIds.length > 1
  twoFactor.form.saveNewPasswordToDb = true
  twoFactor.visible = true

  if (accountIds.length === 1) {
    try {
      const account = await panelApi.account(accountIds[0])
      twoFactor.form.currentPassword = account.twoFactorPassword || ''
    } catch {
      // 失败时保持空密码，后续提交仍按用户输入处理。
    }
  }
}

async function submitTwoFactor() {
  if (!twoFactor.form.newPassword.trim()) {
    ElMessage.warning('新二级密码不能为空')
    return
  }
  if (twoFactor.form.newPassword !== twoFactor.form.confirmPassword) {
    ElMessage.warning('两次输入的新二级密码不一致')
    return
  }
  await ElMessageBox.confirm(`将对 ${twoFactor.accountIds.length} 个账号修改二级密码。是否继续？`, '确认修改', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })
  twoFactor.running = true
  try {
    const result = await panelApi.changeTwoFactorPassword({
      accountIds: twoFactor.accountIds,
      currentPassword: twoFactor.form.currentPassword,
      newPassword: twoFactor.form.newPassword,
      hint: twoFactor.form.hint,
      useStoredPasswords: twoFactor.form.useStoredPasswords,
      saveNewPasswordToDb: twoFactor.form.saveNewPasswordToDb,
    })
    twoFactor.visible = false
    showBatchResult('二级密码修改完成', result)
  } finally {
    twoFactor.running = false
  }
}

async function requestTwoFactorReset() {
  await ElMessageBox.confirm(
    `将对 ${twoFactor.accountIds.length} 个账号向 Telegram 发起重置二级密码申请，通常需要等待 7 天。是否继续？`,
    '确认申请重置',
    { type: 'warning', confirmButtonText: '继续', cancelButtonText: '取消' },
  )
  twoFactor.running = true
  try {
    const result = await panelApi.requestTwoFactorPasswordReset(twoFactor.accountIds)
    showBatchResult('二级密码重置申请结果', result)
  } finally {
    twoFactor.running = false
  }
}

async function openEmailDialog(kind: 'recovery' | 'login', row: Row) {
  emailDialog.kind = kind
  emailDialog.row = row
  emailDialog.title = kind === 'recovery' ? '绑定/换绑找回邮箱' : '绑定/换绑登录邮箱'
  emailDialog.currentPassword = ''
  emailDialog.email = ''
  emailDialog.code = ''
  emailDialog.statusText = ''
  emailDialog.hasPendingRecoveryEmail = false
  emailDialog.canOpenTwoFactor = false
  emailDialog.visible = true
  try {
    if (kind === 'recovery') {
      const status = await panelApi.twoFactorRecoveryEmailStatus(row.id)
      emailDialog.hasPendingRecoveryEmail = !!status.unconfirmedEmailPattern
      emailDialog.canOpenTwoFactor = status.success && !status.hasTwoFactorPassword
      emailDialog.statusText = status.success
        ? `二级密码：${status.hasTwoFactorPassword ? '已开启' : '未开启'}；找回邮箱：${status.hasRecoveryEmail ? '已绑定' : '未绑定'}${status.unconfirmedEmailPattern ? `；待确认：${status.unconfirmedEmailPattern}` : ''}`
        : status.error || ''
    } else {
      const status = await panelApi.loginEmailStatus(row.id)
      emailDialog.statusText = status.success
        ? `${status.hasLoginEmail ? '已启用登录邮箱' : '未启用登录邮箱'}${status.loginEmailPattern ? `：${status.loginEmailPattern}` : ''}`
        : status.error || ''
    }
  } catch {
    // 错误已由拦截器提示
  }
}

async function resendRecoveryEmailCode() {
  if (!emailDialog.row) return
  emailDialog.sending = true
  try {
    const result = await panelApi.resendTwoFactorRecoveryEmail(emailDialog.row.id)
    emailDialog.statusText = result.emailPattern ? `验证码已重发：${result.emailPattern}` : '验证码已重发'
    emailDialog.hasPendingRecoveryEmail = true
    ElMessage.success('验证码已重发')
  } finally {
    emailDialog.sending = false
  }
}

async function resendLoginEmailCode() {
  if (!emailDialog.row) return
  await sendEmailCode()
}

async function cancelRecoveryEmail() {
  if (!emailDialog.row) return
  await ElMessageBox.confirm('将取消当前待确认的找回邮箱。是否继续？', '确认取消验证', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })
  emailDialog.sending = true
  try {
    await panelApi.cancelTwoFactorRecoveryEmail(emailDialog.row.id)
    emailDialog.statusText = '已取消待确认找回邮箱'
    emailDialog.hasPendingRecoveryEmail = false
    ElMessage.success('已取消验证')
  } finally {
    emailDialog.sending = false
  }
}

function openTwoFactorFromEmail() {
  if (!emailDialog.row) return
  emailDialog.visible = false
  openTwoFactor([emailDialog.row.id])
}

async function sendEmailCode() {
  if (!emailDialog.row) return
  if (!emailDialog.email.trim()) {
    ElMessage.warning('请填写邮箱')
    return
  }
  emailDialog.sending = true
  try {
    const result =
      emailDialog.kind === 'recovery'
        ? await panelApi.setTwoFactorRecoveryEmail(emailDialog.row.id, {
            currentPassword: emailDialog.currentPassword,
            email: emailDialog.email,
          })
        : await panelApi.setLoginEmail(emailDialog.row.id, emailDialog.email)
    emailDialog.statusText = result.emailPattern ? `验证码已发送：${result.emailPattern}` : '验证码已发送'
    ElMessage.success('验证码已发送')
  } finally {
    emailDialog.sending = false
  }
}

async function confirmEmailCode() {
  if (!emailDialog.row) return
  if (!emailDialog.code.trim()) {
    ElMessage.warning('请填写验证码')
    return
  }
  emailDialog.confirming = true
  try {
    if (emailDialog.kind === 'recovery') {
      await panelApi.confirmTwoFactorRecoveryEmail(emailDialog.row.id, emailDialog.code)
    } else {
      await panelApi.confirmLoginEmail(emailDialog.row.id, emailDialog.code)
    }
    ElMessage.success('邮箱已确认')
    emailDialog.visible = false
  } finally {
    emailDialog.confirming = false
  }
}

function openBatchProfile(mode: 'nickname' | 'bio' | 'username' | 'avatar') {
  if (!ensureSelected()) return
  batchProfile.mode = mode
  batchProfile.value = ''
  batchProfile.appendPhoneLast4WhenDuplicate = true
  batchProfile.avatarSource = 'fixed'
  batchProfile.avatarFile = null
  batchProfile.avatarFiles = []
  batchProfile.dictionaryName = imageDictionaries.value[0]?.name || ''
  batchProfile.title =
    mode === 'nickname'
      ? '批量修改昵称'
      : mode === 'bio'
        ? '批量修改 Bio'
        : mode === 'username'
          ? '批量修改用户名'
          : '批量修改头像'
  batchProfile.visible = true
}

async function submitBatchProfile() {
  if (batchProfile.mode === 'avatar') {
    await submitBatchAvatar()
    return
  }

  if (batchProfile.mode !== 'bio' && parseTemplateLines(batchProfile.value).length === 0) {
    ElMessage.warning('请填写内容')
    return
  }
  if (batchProfile.mode === 'username' && selectedIds.value.length > 1 && !/\{[a-zA-Z0-9_]+\}/.test(batchProfile.value)) {
    ElMessage.warning('批量修改多个账号时，用户名模板至少需要包含一个变量')
    return
  }

  const safeIds = await confirmSensitiveBatchRisk(selectedIds.value)
  if (!safeIds) return

  await ElMessageBox.confirm(`将对 ${safeIds.length} 个账号执行${batchProfile.title}。是否继续？`, '确认修改', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })

  batchProfile.running = true
  try {
    const result = await panelApi.batchUpdateProfile({
      accountIds: safeIds,
      mode: batchProfile.mode as 'nickname' | 'bio' | 'username',
      nickname: batchProfile.mode === 'nickname' ? batchProfile.value : null,
      nicknameTemplates: batchProfile.mode === 'nickname' ? parseTemplateLines(batchProfile.value) : null,
      appendPhoneLast4WhenDuplicate: batchProfile.mode === 'nickname' ? batchProfile.appendPhoneLast4WhenDuplicate : null,
      bio: batchProfile.mode === 'bio' ? batchProfile.value : null,
      usernameTemplate: batchProfile.mode === 'username' ? batchProfile.value : null,
    })
    batchProfile.visible = false
    showBatchResult(`${batchProfile.title}完成`, result)
    await load()
  } finally {
    batchProfile.running = false
  }
}

async function submitBatchAvatar() {
  if (batchProfile.avatarSource === 'fixed' && !batchProfile.avatarFile) {
    ElMessage.warning('请先选择头像图片')
    return
  }
  if (batchProfile.avatarSource === 'dictionary' && !batchProfile.dictionaryName.trim()) {
    ElMessage.warning('请选择图片字典')
    return
  }

  const safeIds = await confirmSensitiveBatchRisk(selectedIds.value)
  if (!safeIds) return

  await ElMessageBox.confirm(
    `将对 ${safeIds.length} 个账号批量修改头像。是否继续？`,
    '确认修改头像',
    {
      type: 'warning',
      confirmButtonText: '继续',
      cancelButtonText: '取消',
    },
  )

  const form = new FormData()
  form.append('accountIds', safeIds.join(','))
  form.append('source', batchProfile.avatarSource)
  if (batchProfile.avatarSource === 'fixed') {
    form.append('avatar', batchProfile.avatarFile!)
  } else {
    form.append('dictionaryName', batchProfile.dictionaryName)
  }

  batchProfile.running = true
  try {
    const result = await panelApi.batchUpdateAvatar(form)
    batchProfile.visible = false
    showBatchResult('批量修改头像完成', result)
    await load()
  } finally {
    batchProfile.running = false
  }
}

async function openMemberships(row: Row, type: 'channels' | 'groups') {
  listDialog.visible = true
  listDialog.loading = true
  listDialog.type = 'memberships'
  listDialog.accountId = row.id
  listDialog.title = type === 'channels' ? '加入的频道' : '加入的群组'
  listDialog.memberships = []
  try {
    listDialog.memberships = type === 'channels' ? await panelApi.accountChannels(row.id) : await panelApi.accountGroups(row.id)
  } finally {
    listDialog.loading = false
  }
}

async function openInbox(row: Row) {
  listDialog.visible = true
  listDialog.loading = true
  listDialog.type = 'messages'
  listDialog.accountId = row.id
  listDialog.title = `系统通知（验证码） - ${row.displayPhone}`
  listDialog.messages = []
  try {
    listDialog.messages = await panelApi.systemMessages(row.id, 30)
  } finally {
    listDialog.loading = false
  }
}

async function openDevices(row: Row) {
  listDialog.visible = true
  listDialog.loading = true
  listDialog.type = 'devices'
  listDialog.accountId = row.id
  listDialog.title = `在线设备 - ${row.displayPhone}`
  listDialog.devices = []
  try {
    listDialog.devices = await panelApi.devices(row.id)
  } finally {
    listDialog.loading = false
  }
}

async function kickDevice(device: TelegramAuthorization) {
  if (device.current) return
  await ElMessageBox.confirm(`确定要踢出该设备登录吗？\n${device.title}\nIP：${device.ip || '-'}`, '确认踢出', {
    type: 'warning',
    confirmButtonText: '踢出',
    cancelButtonText: '取消',
  })
  await panelApi.kickDevice(listDialog.accountId, device.hash)
  ElMessage.success('已踢出该设备')
  listDialog.devices = await panelApi.devices(listDialog.accountId)
}

async function kickAllDevicesForDialog() {
  await ElMessageBox.confirm('确定要踢出所有其他设备吗？（当前设备会保留）', '确认踢出', {
    type: 'warning',
    confirmButtonText: '踢出',
    cancelButtonText: '取消',
  })
  await panelApi.kickAllOtherDevices(listDialog.accountId)
  ElMessage.success('已踢出所有其他设备')
  listDialog.devices = await panelApi.devices(listDialog.accountId)
}

function handleBatchCommand(command: string) {
  if (command !== 'export-page' && command !== 'export-selected' && !ensureSelected()) return
  const ids = selectedIds.value
  const nonConflictingCommands = new Set(['export-selected', 'export-page'])
  if (!nonConflictingCommands.has(command) && !ensureAccountsIdle(ids, '执行该批量操作')) return
  switch (command) {
    case 'batch-join':
      openChatDialog('join', ids)
      break
    case 'batch-leave':
      openChatDialog('leave', ids)
      break
    case 'two-factor':
      openTwoFactor(ids)
      break
    case 'recovery-email':
      openBatchRecoveryEmail()
      break
    case 'kick-devices':
      batchKickDevices()
      break
    case 'category':
      openBatchCategory()
      break
    case 'proxy':
      openAccountProxy(ids)
      break
    case 'nickname':
      openBatchProfile('nickname')
      break
    case 'avatar':
      openBatchProfile('avatar')
      break
    case 'username':
      openBatchProfile('username')
      break
    case 'bio':
      openBatchProfile('bio')
      break
    case 'delete':
      deleteSelected()
      break
    case 'export-selected':
      openExport(selectedIds.value, '已选账号')
      break
    case 'export-page':
      openExport(rows.value.map((x) => x.id), '当前页账号')
      break
  }
}

function handleRowCommand(command: string, row: Row) {
  const readOnlyCommands = new Set(['details', 'refresh', 'channels', 'groups', 'proxy-egress'])
  if (!readOnlyCommands.has(command) && !ensureAccountsIdle([row.id], '执行该操作')) return
  switch (command) {
    case 'details':
      openDetails(row)
      break
    case 'profile':
      openProfile(row)
      break
    case 'refresh':
      refreshStatus(row)
      break
    case 'join':
      openChatDialog('join', [row.id])
      break
    case 'leave':
      openChatDialog('leave', [row.id])
      break
    case 'channels':
      openMemberships(row, 'channels')
      break
    case 'groups':
      openMemberships(row, 'groups')
      break
    case 'inbox':
      openInbox(row)
      break
    case 'devices':
      openDevices(row)
      break
    case 'proxy':
      openAccountProxy([row.id], row)
      break
    case 'proxy-egress':
      checkAccountProxyEgress(row)
      break
    case 'two-factor':
      openTwoFactor([row.id])
      break
    case 'recovery-email':
      openEmailDialog('recovery', row)
      break
    case 'login-email':
      openEmailDialog('login', row)
      break
    case 'toggle':
      toggleActive(row)
      break
    case 'delete':
      deleteOne(row)
      break
  }
}

function openExport(ids: number[], scopeLabel: string) {
  if (ids.length === 0) {
    ElMessage.info(scopeLabel === '已选账号' ? '请先勾选要导出的账号' : '当前页没有账号可导出')
    return
  }
  exportDialog.ids = ids
  exportDialog.scopeLabel = scopeLabel
  exportDialog.visible = true
}

function exportAccounts(format: 'telethon' | 'tdata') {
  const qs = exportDialog.ids.join(',')
  const ts = Date.now()
  window.location.href = `/downloads/accounts.zip?ids=${encodeURIComponent(qs)}&format=${format}&ts=${ts}`
  exportDialog.visible = false
}

function parseTemplateLines(text: string) {
  return text
    .split(/\r\n|\n|\r/)
    .map((x) => x.trim())
    .filter(Boolean)
}

async function confirmSensitiveBatchRisk(ids: number[]) {
  return confirmChatMembershipRisk(ids)
}

function ensureSelected() {
  if (selectedIds.value.length === 0) {
    ElMessage.info('请先选择账号')
    return false
  }
  return true
}

function showBatchResult(title: string, result: AccountBatchOperationResult) {
  resultDialog.title = title
  resultDialog.summary = `成功 ${result.success}，失败 ${result.failed}`
  resultDialog.items = result.items
  resultDialog.visible = true
  if (result.failed === 0) ElMessage.success(resultDialog.summary)
  else ElMessage.warning(resultDialog.summary)
}

watch(filters, () => {
  window.clearTimeout(filterTimer)
  filterTimer = window.setTimeout(() => {
    page.value = 1
    load()
  }, 300)
})

onMounted(async () => {
  await Promise.all([loadCategories(), loadDictionaries(), loadProxies(), loadWarpStatus(), load()])
})
</script>

<style scoped>
.accounts-page {
  min-width: 0;
}

.action-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
  margin-top: 12px;
  max-width: 100%;
  overflow-x: auto;
  padding-bottom: 2px;
}

.action-bar :deep(.el-button) {
  margin-left: 0;
  flex: 0 0 auto;
}

.accounts-table {
  width: 100%;
}

.account-category-tag {
  border-radius: 999px;
}

.proxy-cell {
  min-width: 0;
}

.proxy-name {
  margin-left: 6px;
  overflow-wrap: anywhere;
}

.proxy-account-count {
  margin-top: 12px;
}

.proxy-route-options {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  width: 100%;
}

.proxy-route-options :deep(.el-radio-button),
.proxy-route-options :deep(.el-radio-button__inner) {
  width: 100%;
}

.mobile-account-proxy {
  display: flex;
  align-items: center;
  gap: 4px;
  min-width: 0;
  margin-top: 4px;
  white-space: nowrap;
}

.mobile-account-proxy .proxy-name {
  max-width: 72px;
  margin-left: 0;
  overflow: hidden;
  text-overflow: ellipsis;
}

.row-actions {
  display: flex;
  align-items: center;
  gap: 2px;
}

.ellipsis {
  display: inline-block;
  max-width: 150px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  vertical-align: bottom;
}

.dialog-account {
  margin: 12px 0;
  color: var(--tp-muted);
}

.account-list-dialog :deep(.el-dialog__body) {
  overflow-x: hidden;
}

.devices-table {
  width: 100%;
}

.devices-table :deep(.el-table__body-wrapper),
.devices-table :deep(.el-scrollbar__wrap) {
  overflow-x: hidden;
}

.device-title,
.device-text,
.device-meta {
  overflow-wrap: anywhere;
  word-break: break-word;
  white-space: normal;
  line-height: 1.35;
}

.device-api-line {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 6px 0 4px;
}

.device-help-tag {
  margin-top: 5px;
  cursor: help;
}

.message-item {
  padding: 10px 0;
  border-bottom: 1px solid var(--tp-border);
}

.message-text {
  margin-top: 4px;
  white-space: pre-wrap;
  line-height: 1.5;
}

.result-summary {
  margin-bottom: 12px;
  color: var(--tp-muted);
}

.export-options {
  display: grid;
  grid-template-columns: 1fr;
  gap: 12px;
  margin-top: 14px;
}
</style>
