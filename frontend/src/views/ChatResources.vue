<template>
  <div>
    <el-card shadow="never" class="page-card">
      <div class="toolbar">
        <el-select v-model="filters.accountId" class="resource-filter" placeholder="筛选账号" @change="onAccountFilterChanged">
          <el-option label="全部账号" :value="0" />
          <el-option v-for="account in accounts" :key="account.id" :label="accountLabel(account)" :value="account.id" />
        </el-select>
        <el-select v-model="filters.type" class="filter" :placeholder="`${kindName}类型`" @change="reloadFirst">
          <el-option label="全部" value="all" />
          <el-option label="公开" value="public" />
          <el-option label="私密" value="private" />
        </el-select>
        <el-select v-model="filters.role" class="filter" placeholder="账号角色" @change="reloadFirst">
          <el-option label="全部角色" value="all" />
          <el-option :label="kind === 'group' ? '我创建' : '创建者'" value="creator" />
          <el-option :label="kind === 'group' ? '我管理' : '管理员'" value="admin" />
          <el-option label="非管理员" value="member" />
        </el-select>
        <el-select v-model="filters.categoryId" class="filter" :placeholder="`${kindName}分类`" @change="reloadFirst">
          <el-option label="全部分类" :value="-1" />
          <el-option label="未分类" :value="0" />
          <el-option v-for="category in categories" :key="category.id" :label="category.name" :value="category.id" />
        </el-select>
        <el-input v-model="filters.search" class="search" :placeholder="`搜索${kindName}...`" clearable @input="debouncedLoad" />
      </div>
    </el-card>

    <el-card shadow="never" class="page-card mt-4">
      <div class="toolbar mb-3">
        <el-button :icon="Select" :disabled="loading || rows.length === 0" @click="toggleSelection">{{ selectionText }}</el-button>
        <el-button v-if="kind === 'group'" :icon="Plus" @click="router.push('/groups/create')">创建群组</el-button>
        <el-button v-if="kind === 'group'" :icon="Folder" @click="router.push('/groups/categories')">分类管理</el-button>
        <el-button :icon="Refresh" :disabled="loading || filters.accountId <= 0" @click="syncCurrent">同步当前</el-button>
        <el-button :icon="Refresh" :disabled="loading" @click="syncAll">同步全部</el-button>
        <el-dropdown :disabled="loading">
          <el-button>
            批量操作<el-icon class="el-icon--right"><ArrowDown /></el-icon>
          </el-button>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item :disabled="selectedIds.length === 0" @click="openChatInvite(selectedIds)">批量邀请（已选）</el-dropdown-item>
              <el-dropdown-item :disabled="selectedIds.length === 0" @click="batchCopyLinks">批量复制{{ linkName }}（已选）</el-dropdown-item>
              <el-dropdown-item :disabled="selectedIds.length === 0" @click="batchExportInvites">批量导出{{ linkName }}（已选）</el-dropdown-item>
              <el-dropdown-item :disabled="selectedIds.length === 0" @click="openChatAdmins(selectedIds)">批量设置管理员（已选）</el-dropdown-item>
              <el-dropdown-item :disabled="selectedIds.length === 0" @click="openBatchCategory">批量修改分类（已选）</el-dropdown-item>
              <el-dropdown-item divided :disabled="selectedIds.length === 0" @click="batchLeave">批量退出{{ kindName }}（已选）</el-dropdown-item>
              <el-dropdown-item :disabled="selectedIds.length === 0" @click="batchDisband">批量解散{{ kindName }}（已选）</el-dropdown-item>
              <el-dropdown-item v-if="kind === 'group'" :disabled="selectedIds.length === 0" @click="batchDelete">批量删除（已选）</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
        <ColumnVisibilityMenu
          v-model="visibleColumnKeys"
          :columns="chatResourceColumns"
          :disabled="loading"
          @reset="resetColumns"
          @show-all="showAllColumns"
        />
        <el-tag v-if="selectedIds.length" type="primary" effect="plain">已选 {{ selectedIds.length }}</el-tag>
        <span v-else class="muted">{{ kindName }} ({{ total }})</span>
      </div>

      <el-table ref="tableRef" v-loading="loading" :data="rows" stripe row-key="id" @selection-change="onSelectionChange">
        <el-table-column type="selection" width="46" />
        <el-table-column v-if="isColumnVisible('title')" :label="`${kindName}名称`" min-width="240">
          <template #default="{ row }">
            <div class="cell-main">{{ row.title }}</div>
            <div v-if="row.about" class="cell-sub">{{ truncate(row.about, 56) }}</div>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('username')" label="用户名" min-width="140">
          <template #default="{ row }">
            <el-tag v-if="row.username" size="small">@{{ row.username }}</el-tag>
            <span v-else>-</span>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('type')" label="类型" width="90">
          <template #default="{ row }">
            <el-tag :type="row.username ? 'success' : 'warning'" size="small">{{ row.username ? '公开' : '私密' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('category')" label="分类" min-width="120">
          <template #default="{ row }">
            <el-tag :type="rowCategoryId(row) ? 'primary' : 'info'" size="small">{{ rowCategoryName(row) || '未分类' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('memberCount')" prop="memberCount" label="成员数" width="90" />
        <el-table-column v-if="filters.accountId > 0 && isColumnVisible('currentRole')" label="当前账号角色" width="120">
          <template #default="{ row }">
            <el-tag size="small" :type="roleTagType(currentRole(row))">{{ currentRoleText(row) }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('creator')" label="创建账号" min-width="130">
          <template #default="{ row }">{{ row.creatorDisplayPhone || (row.creatorAccountId ? `账号 ${row.creatorAccountId}` : '-') }}</template>
        </el-table-column>
        <el-table-column v-if="isColumnVisible('syncedAt')" label="最后同步" width="180">
          <template #default="{ row }">{{ formatTime(row.syncedAt) }}</template>
        </el-table-column>
        <el-table-column label="操作" :width="isCompactList ? 70 : 280" fixed="right">
          <template #default="{ row }">
            <div class="row-actions">
              <el-tooltip v-if="!isCompactList" content="查看详情" placement="top">
                <el-button link type="primary" :icon="View" @click="showDetails(row)" />
              </el-tooltip>
              <el-tooltip v-if="!isCompactList" :content="`编辑${kindName}`" placement="top">
                <el-button link type="primary" :icon="Edit" @click="openEdit(row)" />
              </el-tooltip>
              <el-dropdown>
                <el-button link :icon="MoreFilled" />
                <template #dropdown>
                  <el-dropdown-menu>
                    <el-dropdown-item v-if="isCompactList" :icon="View" @click="showDetails(row)">查看详情</el-dropdown-item>
                    <el-dropdown-item v-if="isCompactList" :icon="Edit" @click="openEdit(row)">编辑{{ kindName }}</el-dropdown-item>
                    <el-dropdown-item @click="openChatInvite([row.id])">邀请成员</el-dropdown-item>
                    <el-dropdown-item @click="openChatAdmins([row.id])">设置管理员</el-dropdown-item>
                    <el-dropdown-item @click="openChatKick([row.id])">踢人</el-dropdown-item>
                    <el-dropdown-item @click="openSingleCategory(row)">修改分类</el-dropdown-item>
                    <el-dropdown-item @click="showSystemAccounts(row)">本系统账号</el-dropdown-item>
                    <el-dropdown-item @click="copyLink(row)">复制链接</el-dropdown-item>
                    <el-dropdown-item divided @click="openTransferOwner(row)">转让所有权</el-dropdown-item>
                    <el-dropdown-item @click="leaveOne(row)">退出{{ kindName }}</el-dropdown-item>
                    <el-dropdown-item @click="disbandOne(row)">解散{{ kindName }}</el-dropdown-item>
                    <el-dropdown-item @click="deleteOne(row)">删除{{ kindName }}</el-dropdown-item>
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
          :page-sizes="[20, 50, 100, 200]"
          layout="total, sizes, prev, pager, next, jumper"
          :total="total"
          @size-change="load"
          @current-change="load"
        />
      </div>
    </el-card>

    <el-dialog v-model="detail.visible" :title="`${kindName}详情`" width="860px">
      <el-skeleton v-if="detail.loading" :rows="8" animated />
      <template v-else-if="detail.row">
      <el-descriptions :column="2" border>
        <el-descriptions-item label="名称">{{ detail.row.title }}</el-descriptions-item>
        <el-descriptions-item label="Telegram ID">{{ detail.row.telegramId }}</el-descriptions-item>
        <el-descriptions-item label="用户名">{{ detail.row.username ? `@${detail.row.username}` : '-' }}</el-descriptions-item>
        <el-descriptions-item label="分类">{{ rowCategoryName(detail.row) || '未分类' }}</el-descriptions-item>
        <el-descriptions-item label="成员数">{{ detail.row.memberCount }}</el-descriptions-item>
        <el-descriptions-item label="创建者">{{ detail.row.creatorDisplayPhone || (detail.row.creatorAccountId ? `账号 ${detail.row.creatorAccountId}` : '（非系统创建）') }}</el-descriptions-item>
        <el-descriptions-item label="创建时间">{{ formatTime(detail.row.createdAt || detail.row.systemCreatedAtUtc) }}</el-descriptions-item>
        <el-descriptions-item label="最后同步">{{ formatTime(detail.row.syncedAt) }}</el-descriptions-item>
        <el-descriptions-item label="简介">{{ detail.row.about || '-' }}</el-descriptions-item>
      </el-descriptions>

      <el-divider content-position="left">管理员</el-divider>
      <div class="toolbar compact mb-3">
        <el-button :icon="Refresh" :loading="detail.adminsLoading" :disabled="detail.adminKickLoadingGroupId !== null" @click="loadDetailAdmins">刷新</el-button>
        <span class="muted">{{ detail.admins.length ? `共 ${detail.admins.length} 个管理员` : '暂无管理员数据（或无权限获取）' }}</span>
      </div>
      <el-table v-if="detail.admins.length" :data="detail.admins" stripe max-height="240">
        <el-table-column label="身份" width="100">
          <template #default="{ row }">
            <el-tag size="small" :type="row.isCreator ? 'success' : 'primary'">{{ row.isCreator ? '创建者' : '管理员' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="昵称" min-width="160" prop="displayName" />
        <el-table-column label="用户名" min-width="150">
          <template #default="{ row }">{{ row.username ? `@${row.username}` : '-' }}</template>
        </el-table-column>
        <el-table-column label="头衔" min-width="120">
          <template #default="{ row }">{{ row.rank || '-' }}</template>
        </el-table-column>
        <el-table-column v-if="kind === 'group'" label="操作" width="72" align="center">
          <template #default="{ row }">
            <el-tooltip v-if="!row.isCreator" content="踢出管理员" placement="top">
              <el-button
                link
                type="danger"
                :icon="Delete"
                :disabled="detail.adminKickLoadingGroupId !== null"
                :loading="detail.adminKickLoadingGroupId === detail.row.id && detail.adminKickLoadingUserId === row.userId"
                @click="kickAdminFromDetail(row)"
              />
            </el-tooltip>
            <span v-else>-</span>
          </template>
        </el-table-column>
      </el-table>

      <el-divider content-position="left">本系统账号</el-divider>
      <div class="toolbar compact mb-3">
        <el-button @click="showDetailSystemAccounts">查看完整列表</el-button>
        <el-button :icon="Refresh" :loading="detail.accountsLoading" @click="loadDetailAccounts">刷新</el-button>
        <span class="muted">{{ detail.accounts.length ? `共 ${detail.accounts.length} 个本系统账号` : '暂无本系统账号关联' }}</span>
      </div>
      <el-table v-if="detail.accounts.length" :data="detail.accounts" stripe max-height="240">
        <el-table-column label="手机号" min-width="160">
          <template #default="{ row }">{{ row.displayPhone || `账号 ${row.accountId}` }}</template>
        </el-table-column>
        <el-table-column label="角色" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="roleTagType(row)">{{ row.isCreator ? '创建者' : row.isAdmin ? '管理员' : '成员' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最后同步" min-width="170">
          <template #default="{ row }">{{ formatTime(row.syncedAt) }}</template>
        </el-table-column>
      </el-table>

      <template>
        <el-divider content-position="left">踢出成员</el-divider>
        <el-alert class="mb-3" type="info" :closable="false" show-icon>
          <template #title>输入 @用户名 或用户 ID 执行踢出；非永久会用短时间封禁达到踢出效果，随后自动解除。</template>
        </el-alert>
        <el-form label-position="top">
          <el-form-item label="执行账号">
            <el-select v-model="detail.kickAccountId" class="full">
              <el-option :label="`自动选择（按${kindName}）`" :value="0" />
              <el-option v-for="account in accounts" :key="account.id" :label="accountLabel(account)" :value="account.id" />
            </el-select>
          </el-form-item>
          <el-form-item label="用户名或用户 ID">
            <el-input v-model="detail.kickTarget" placeholder="@username、username 或数字用户 ID" />
          </el-form-item>
          <el-checkbox v-model="detail.kickPermanent">永久封禁</el-checkbox>
          <el-button class="mt-3" type="danger" :loading="detail.kickLoading" @click="kickFromDetail">踢出</el-button>
        </el-form>
      </template>
      </template>
      <template #footer>
        <el-button v-if="detail.row" @click="copyLink(detail.row)">复制链接</el-button>
        <el-button v-if="detail.row" type="primary" plain @click="openEditFromDetail">编辑{{ kindName }}</el-button>
        <el-button @click="detail.visible = false">关闭</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="systemAccounts.visible" title="本系统账号" width="760px">
      <div class="muted mb-3">共 {{ systemAccounts.accounts.length }} 个本系统账号</div>
      <el-table v-if="systemAccounts.accounts.length" :data="systemAccounts.accounts" stripe max-height="420">
        <el-table-column label="手机号" min-width="160">
          <template #default="{ row }">{{ row.displayPhone || `账号 ${row.accountId}` }}</template>
        </el-table-column>
        <el-table-column label="角色" width="110">
          <template #default="{ row }">
            <el-tag size="small" :type="roleTagType(row)">
              {{ row.isCreator ? '创建者' : row.isAdmin ? '管理员' : '成员' }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column label="最后同步" min-width="170">
          <template #default="{ row }">{{ formatTime(row.syncedAt) }}</template>
        </el-table-column>
      </el-table>
      <el-empty v-else :description="`该${kindName}当前没有已同步的本系统账号关联`" />
      <template #footer>
        <el-button type="primary" @click="systemAccounts.visible = false">关闭</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="edit.visible" :title="`编辑${kindName}`" width="620px" destroy-on-close>
      <el-form label-width="96px">
        <el-form-item :label="`${kindName}名称`"><el-input v-model="edit.form.title" /></el-form-item>
        <el-form-item label="简介"><el-input v-model="edit.form.about" type="textarea" :rows="3" /></el-form-item>
        <el-form-item label="公开"><el-switch v-model="edit.form.isPublic" /></el-form-item>
        <el-form-item v-if="edit.form.isPublic" label="用户名"><el-input v-model="edit.form.username" placeholder="不带 @" /></el-form-item>
        <el-form-item v-if="kind === 'channel'" label="允许转发/保存内容">
          <el-alert
            v-if="!edit.form.changeForwardingAllowed"
            title="当前面板无法从本地记录判断频道现有保护内容状态；默认不会修改该项。需要调整时先开启下面的开关。"
            type="info"
            :closable="false"
            show-icon
            class="mb-3"
          />
          <el-checkbox v-model="edit.form.changeForwardingAllowed">本次保存时修改该设置</el-checkbox>
          <el-switch v-model="edit.form.forwardingAllowed" />
          <div class="muted mt-2">关闭后为保护内容，禁止转发和保存。</div>
        </el-form-item>
        <el-form-item label="分类">
          <el-select v-model="edit.form.categoryId" class="full">
            <el-option label="未分类" :value="0" />
            <el-option v-for="category in categories" :key="category.id" :label="category.name" :value="category.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="头像">
          <el-upload v-model:file-list="edit.files" :auto-upload="false" :limit="1" accept="image/*">
            <el-button>选择图片</el-button>
          </el-upload>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="edit.saving" @click="edit.visible = false">取消</el-button>
        <el-button type="primary" :loading="edit.saving" @click="saveEdit">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="categoryDialog.visible" title="修改分类" width="420px">
      <el-select v-model="categoryDialog.categoryId" class="full">
        <el-option label="未分类" :value="0" />
        <el-option v-for="category in categories" :key="category.id" :label="category.name" :value="category.id" />
      </el-select>
      <template #footer>
        <el-button @click="categoryDialog.visible = false">取消</el-button>
        <el-button type="primary" @click="saveCategoryChange">保存</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="transferOwner.visible" title="转让所有权" width="620px" destroy-on-close>
      <el-form label-position="top" autocomplete="off" data-lpignore="true" data-1p-ignore="true" data-bwignore="true">
        <div class="autofill-decoy" aria-hidden="true">
          <input tabindex="-1" autocomplete="username" name="username" type="text" />
          <input tabindex="-1" autocomplete="current-password" name="password" type="password" />
        </div>
        <el-alert
          :title="`此操作会把${kindName}创建者转让给目标用户。成功后只能由新的所有者继续管理，无法在面板内撤销。`"
          type="error"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <el-form-item label="当前创建者账号">
          <el-select v-model="transferOwner.accountId" class="full" :disabled="!!transferOwner.row?.creatorAccountId" @change="onTransferExecutorChanged">
            <el-option v-if="transferOwner.row?.creatorAccountId" :label="transferExecutorLabel" :value="transferOwner.row.creatorAccountId" />
            <el-option v-else label="请选择当前创建者账号" :value="0" />
            <el-option v-for="account in accounts" :key="account.id" :label="accountLabel(account)" :value="account.id" />
          </el-select>
          <div class="muted mt-2">优先使用本地记录的创建者账号；没有创建者记录时需要手动选择真实创建者。</div>
        </el-form-item>
        <el-form-item label="新所有者（本系统账号）">
          <el-select v-model="transferOwner.targetAccountId" class="full" filterable clearable placeholder="可选：选择系统账号" @change="onTransferTargetAccountChanged">
            <el-option
              v-for="account in accounts"
              :key="account.id"
              :disabled="!account.username"
              :label="accountLabel(account)"
              :value="account.id"
            />
          </el-select>
          <div class="muted mt-2">选择系统账号会自动填入该账号的用户名，并在成功后更新本地创建者记录。</div>
        </el-form-item>
        <el-form-item label="新所有者用户名">
          <el-input
            v-model="transferOwner.target"
            name="telegram-transfer-target"
            autocomplete="off"
            data-lpignore="true"
            data-1p-ignore="true"
            data-bwignore="true"
            data-form-type="other"
            placeholder="@username 或 username"
          />
        </el-form-item>
        <el-form-item label="当前创建者账号二级密码">
          <el-input
            v-model="transferOwner.password"
            type="password"
            show-password
            name="telegram-transfer-2fa"
            autocomplete="new-password"
            data-lpignore="true"
            data-1p-ignore="true"
            data-bwignore="true"
            data-form-type="other"
            :placeholder="transferOwner.passwordLoading ? '正在读取系统已保存的二级密码' : '若系统已保存，会自动带入；可手动修改'"
          />
          <div class="muted mt-2">留空时后端也会尝试使用该执行账号在系统中保存的二级密码。</div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="transferOwner.running" @click="transferOwner.visible = false">取消</el-button>
        <el-button type="danger" :loading="transferOwner.running" @click="submitTransferOwner">确认转让</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="channelInvite.visible" title="邀请成员" width="640px">
      <el-form label-position="top">
        <el-alert
          title="可以使用单个账号，也可以选择账号分组轮询执行，降低单账号连续邀请触发风控的概率。"
          type="warning"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <div class="preset-row">
          <el-select v-model="channelInvite.presetName" class="full" placeholder="邀请预设" @change="applyInvitePreset">
            <el-option label="（不使用预设）" value="" />
            <el-option v-for="preset in invitePresets" :key="preset.name" :label="preset.name" :value="preset.name" />
          </el-select>
          <el-button type="danger" plain :disabled="!channelInvite.presetName" @click="deleteInvitePreset">删除预设</el-button>
        </div>
        <el-form-item label="执行账号来源">
          <el-radio-group v-model="channelInvite.accountMode">
            <el-radio-button value="auto">自动</el-radio-button>
            <el-radio-button value="account">单账号</el-radio-button>
            <el-radio-button value="category">账号分组轮询</el-radio-button>
          </el-radio-group>
          <div v-if="channelInvite.accountMode === 'auto'" class="muted mt-2">自动模式会按每个{{ kindName }}的创建账号/可执行账号处理。</div>
        </el-form-item>
        <el-form-item v-if="channelInvite.accountMode === 'account'" label="执行账号">
          <el-select v-model="channelInvite.accountId" class="full" filterable>
            <el-option v-for="account in accounts" :key="account.id" :label="accountLabel(account)" :value="account.id" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="channelInvite.accountMode === 'category'" label="账号分组">
          <el-select v-model="channelInvite.accountCategoryId" class="full" filterable>
            <el-option v-for="category in accountCategoryOptions" :key="category.id" :label="category.label" :value="category.id" />
          </el-select>
          <div class="muted mt-2">任务会在该分组的可操作账号中按顺序轮询，每次邀请切换一个账号。</div>
        </el-form-item>
        <el-form-item label="用户名列表">
          <el-input v-model="channelInvite.usernamesText" type="textarea" :rows="7" placeholder="每行一个 username 或 @username" />
        </el-form-item>
        <div class="preset-row">
          <el-input v-model="channelInvite.presetNameToSave" placeholder="保存当前用户名为预设组（名称）" />
          <el-button type="primary" plain :disabled="!channelInvite.presetNameToSave.trim()" @click="saveInvitePreset">保存为预设</el-button>
        </div>
        <el-form-item label="邀请间隔（毫秒）">
          <el-input-number v-model="channelInvite.delayMs" :min="0" :max="30000" :step="500" />
        </el-form-item>
        <div class="muted">目标{{ kindName }}：{{ channelInvite.ids.length }} 个</div>
      </el-form>
      <template #footer>
        <el-button :disabled="channelInvite.running" @click="channelInvite.visible = false">取消</el-button>
        <el-button type="primary" :loading="channelInvite.running" @click="submitChannelInvite">开始邀请</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="channelAdmins.visible" title="设置管理员" width="640px">
      <el-form label-position="top">
        <el-alert
          :title="`将使用执行账号为所选${kindName}批量设置管理员；自动选择时按${kindName}创建账号执行。`"
          type="warning"
          :closable="false"
          show-icon
          class="mb-3"
        />
        <div class="preset-row">
          <el-select v-model="channelAdmins.presetName" class="full" placeholder="管理员预设（用户名列表）" @change="applyAdminPreset">
            <el-option label="（不使用预设）" value="" />
            <el-option v-for="preset in adminPresets" :key="preset.name" :label="preset.name" :value="preset.name" />
          </el-select>
          <el-button type="danger" plain :disabled="!channelAdmins.presetName" @click="deleteAdminPreset">删除预设</el-button>
        </div>
        <el-form-item label="执行账号">
          <el-select v-model="channelAdmins.accountId" class="full">
            <el-option :label="`每个${kindName}创建账号（默认）`" :value="0" />
            <el-option v-for="account in accounts" :key="account.id" :label="accountLabel(account)" :value="account.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="管理员用户名">
          <el-input v-model="channelAdmins.usernamesText" type="textarea" :rows="6" placeholder="每行一个 username 或 @username" />
        </el-form-item>
        <div class="preset-row">
          <el-input v-model="channelAdmins.presetNameToSave" placeholder="保存当前用户名为预设组（名称）" />
          <el-button type="primary" plain :disabled="!channelAdmins.presetNameToSave.trim()" @click="saveAdminPreset">保存为预设</el-button>
        </div>
        <el-form-item label="管理员头衔"><el-input v-model="channelAdmins.adminTitle" /></el-form-item>
        <div class="rights-actions">
          <el-button plain @click="applyBasicChannelAdminRights">常用权限</el-button>
          <el-button plain @click="applyFullChannelAdminRights">全选权限</el-button>
          <el-button plain type="warning" @click="clearChannelAdminRights">清空</el-button>
          <el-button type="primary" plain @click="saveChannelAdminDefaultRights">保存为默认权限</el-button>
        </div>
        <el-form-item label="权限">
          <div class="rights-grid">
            <el-checkbox v-model="channelAdmins.rights.changeInfo">修改信息</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.postMessages">发消息</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.editMessages">编辑消息</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.deleteMessages">删除消息</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.banUsers">封禁用户</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.inviteUsers">邀请用户</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.pinMessages">置顶消息</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.manageCall">语音/直播</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.addAdmins">添加管理员</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.anonymous">匿名管理员</el-checkbox>
            <el-checkbox v-model="channelAdmins.rights.manageTopics">管理话题</el-checkbox>
          </div>
        </el-form-item>
        <el-form-item label="操作间隔（毫秒）">
          <el-input-number v-model="channelAdmins.delayMs" :min="0" :max="30000" :step="500" />
        </el-form-item>
        <div class="muted">目标{{ kindName }}：{{ channelAdmins.ids.length }} 个</div>
      </el-form>
      <template #footer>
        <el-button :disabled="channelAdmins.running" @click="channelAdmins.visible = false">取消</el-button>
        <el-button type="primary" :loading="channelAdmins.running" @click="submitChannelAdmins">开始设置</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="channelKick.visible" title="踢人" width="460px">
      <el-form label-position="top">
        <el-form-item label="执行账号">
          <el-select v-model="channelKick.accountId" class="full">
            <el-option :label="`自动选择（按${kindName}）`" :value="0" />
            <el-option v-for="account in accounts" :key="account.id" :label="accountLabel(account)" :value="account.id" />
          </el-select>
        </el-form-item>
        <el-form-item label="用户名或用户 ID">
          <el-input v-model="channelKick.target" placeholder="@username、username 或数字用户 ID" />
        </el-form-item>
        <el-checkbox v-model="channelKick.permanentBan">封禁（不只是踢出）</el-checkbox>
        <div class="muted mt-2">目标{{ kindName }}：{{ channelKick.ids.length }} 个</div>
      </el-form>
      <template #footer>
        <el-button :disabled="channelKick.running" @click="channelKick.visible = false">取消</el-button>
        <el-button type="danger" :loading="channelKick.running" @click="submitChannelKick">执行</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage, ElMessageBox, type TableInstance, type UploadUserFile } from 'element-plus'
import { ArrowDown, Delete, Edit, Folder, MoreFilled, Plus, Refresh, Select, View } from '@element-plus/icons-vue'
import { panelApi } from '@/api/panel'
import ColumnVisibilityMenu from '@/components/ColumnVisibilityMenu.vue'
import type { ChannelListItem, ChatAdmin, ChatMembershipAccount, GroupListItem, OperationAccount, SimpleCategory, TextPreset } from '@/api/types'
import { formatTime } from '@/utils/format'
import { writeClipboardText } from '@/utils/clipboard'
import { usePersistentColumnVisibility, type ColumnVisibilityOption } from '@/utils/columnVisibility'
import { useMediaQuery } from '@/utils/useMediaQuery'

type Kind = 'channel' | 'group'
type Row = ChannelListItem | GroupListItem

const props = defineProps<{ kind: Kind }>()
const route = useRoute()
const router = useRouter()
const kindName = computed(() => (props.kind === 'channel' ? '频道' : '群组'))
const linkName = computed(() => (props.kind === 'channel' ? '邀请链接' : '加入链接'))

const loading = ref(false)
const rows = ref<Row[]>([])
const total = ref(0)
const page = ref(1)
const pageSize = ref(20)
const accounts = ref<OperationAccount[]>([])
const categories = ref<SimpleCategory[]>([])
const invitePresets = ref<TextPreset[]>([])
const adminPresets = ref<TextPreset[]>([])
const tableRef = ref<TableInstance>()
const selectedRows = ref<Row[]>([])
const isCompactList = useMediaQuery('(max-width: 640px)')
let searchTimer: number | undefined
let selectionMode: 'select' | 'invert' | 'clear' = 'select'

const filters = reactive({
  accountId: readPositiveQueryNumber(route.query.accountId),
  type: 'all',
  role: 'all',
  categoryId: -1,
  search: '',
})

const chatResourceColumns: ColumnVisibilityOption[] = [
  { key: 'title', label: `${kindName.value}名称` },
  { key: 'username', label: '用户名' },
  { key: 'type', label: '类型' },
  { key: 'category', label: '分类' },
  { key: 'memberCount', label: '成员数' },
  { key: 'currentRole', label: '当前账号角色' },
  { key: 'creator', label: '创建账号' },
  { key: 'syncedAt', label: '最后同步' },
]

const { visibleColumnKeys, isColumnVisible, resetColumns, showAllColumns } = usePersistentColumnVisibility(
  `telegram-panel.${props.kind}.columns`,
  chatResourceColumns,
)

const detail = reactive({
  visible: false,
  loading: false,
  adminsLoading: false,
  accountsLoading: false,
  row: null as Row | null,
  admins: [] as ChatAdmin[],
  accounts: [] as ChatMembershipAccount[],
  kickAccountId: 0,
  kickTarget: '',
  kickPermanent: false,
  kickLoading: false,
  adminKickLoadingGroupId: null as number | null,
  adminKickLoadingUserId: null as number | null,
})
const systemAccounts = reactive({
  visible: false,
  accounts: [] as ChatMembershipAccount[],
})
const edit = reactive({
  visible: false,
  saving: false,
  row: null as Row | null,
  form: { title: '', about: '', isPublic: false, username: '', categoryId: 0, changeForwardingAllowed: false, forwardingAllowed: true },
  files: [] as UploadUserFile[],
})
const categoryDialog = reactive({
  visible: false,
  ids: [] as number[],
  categoryId: 0,
})
const transferOwner = reactive({
  visible: false,
  running: false,
  passwordLoading: false,
  row: null as Row | null,
  accountId: 0,
  targetAccountId: null as number | null,
  target: '',
  password: '',
})
const channelInvite = reactive({
  visible: false,
  running: false,
  ids: [] as number[],
  accountMode: 'auto' as 'auto' | 'account' | 'category',
  accountId: 0,
  accountCategoryId: 0,
  presetName: '',
  presetNameToSave: '',
  usernamesText: '',
  delayMs: 2000,
})
const channelAdmins = reactive({
  visible: false,
  running: false,
  ids: [] as number[],
  accountId: 0,
  presetName: '',
  presetNameToSave: '',
  usernamesText: '',
  adminTitle: 'Admin',
  delayMs: 1500,
  rights: {
    changeInfo: true,
    postMessages: true,
    editMessages: true,
    deleteMessages: true,
    banUsers: true,
    inviteUsers: true,
    pinMessages: true,
    manageCall: false,
    addAdmins: false,
    anonymous: false,
    manageTopics: false,
  },
})
const channelKick = reactive({
  visible: false,
  running: false,
  ids: [] as number[],
  accountId: 0,
  target: '',
  permanentBan: false,
})

const selectedIds = computed(() => selectedRows.value.map((x) => x.id))
const selectionText = computed(() => selectionMode === 'select' ? '全选本页' : selectionMode === 'invert' ? '反选本页' : '清空选择')
const accountCategoryOptions = computed(() => {
  const grouped = new Map<number, { id: number; name: string; count: number }>()
  for (const account of accounts.value) {
    if (!account.categoryId || account.categoryId <= 0) continue
    const item = grouped.get(account.categoryId) || {
      id: account.categoryId,
      name: account.categoryName || `分组 #${account.categoryId}`,
      count: 0,
    }
    item.count += 1
    grouped.set(account.categoryId, item)
  }
  return Array.from(grouped.values())
    .sort((a, b) => a.name.localeCompare(b.name, 'zh-Hans-CN'))
    .map((x) => ({ id: x.id, label: `${x.name}（${x.count} 个账号）` }))
})
const transferExecutorLabel = computed(() => {
  const id = transferOwner.row?.creatorAccountId
  if (!id) return '请选择当前创建者账号'
  const account = accounts.value.find((x) => x.id === id)
  return account ? accountLabel(account) : transferOwner.row?.creatorDisplayPhone || `账号 ${id}`
})

async function loadMeta() {
  accounts.value = await panelApi.operationAccounts()
  categories.value = props.kind === 'channel' ? await panelApi.channelGroups() : await panelApi.groupCategories()
  await Promise.all([loadChannelPresets(), loadChannelAdminDefaults()])
}

async function load() {
  loading.value = true
  try {
    const params = {
      page: page.value,
      pageSize: pageSize.value,
      accountId: filters.accountId || null,
      filterType: filters.type,
      membershipRole: filters.role,
      search: filters.search,
    }
    const result = props.kind === 'channel'
      ? await panelApi.channels({ ...params, groupId: filters.categoryId })
      : await panelApi.groups({ ...params, categoryId: filters.categoryId })
    rows.value = result.items
    total.value = result.total
    selectedRows.value = []
  } finally {
    loading.value = false
  }
}

function reloadFirst() {
  page.value = 1
  load()
}

function onAccountFilterChanged() {
  syncAccountQuery()
  reloadFirst()
}

function syncAccountQuery() {
  const nextQuery = { ...route.query }
  if (filters.accountId > 0) nextQuery.accountId = String(filters.accountId)
  else delete nextQuery.accountId
  router.replace({ query: nextQuery })
}

function debouncedLoad() {
  if (searchTimer) window.clearTimeout(searchTimer)
  searchTimer = window.setTimeout(reloadFirst, 300)
}

function accountLabel(account: OperationAccount) {
  const name = account.nickname || account.displayPhone
  return account.username ? `#${account.displayNumber} ${name} (@${account.username})` : `#${account.displayNumber} ${name}`
}

function rowCategoryId(row: Row) {
  return props.kind === 'channel' ? (row as ChannelListItem).groupId : (row as GroupListItem).categoryId
}

function rowCategoryName(row: Row) {
  return props.kind === 'channel' ? (row as ChannelListItem).groupName : (row as GroupListItem).categoryName
}

function currentRole(row: Row): ChatMembershipAccount | undefined {
  return row.accounts.find((x) => x.accountId === filters.accountId)
}

function currentRoleText(row: Row) {
  const role = currentRole(row)
  if (!role) return '未同步'
  if (role.isCreator) return '创建者'
  if (role.isAdmin) return '管理员'
  return '成员'
}

function roleTagType(role?: ChatMembershipAccount) {
  if (!role) return 'info'
  if (role.isCreator) return 'success'
  if (role.isAdmin) return 'primary'
  return 'info'
}

function truncate(value: string, len: number) {
  return value.length > len ? `${value.slice(0, len)}...` : value
}

function onSelectionChange(selection: Row[]) {
  selectedRows.value = selection
}

function toggleSelection() {
  if (!tableRef.value) return
  if (selectionMode === 'select') {
    rows.value.forEach((row) => tableRef.value?.toggleRowSelection(row, true))
    selectionMode = 'invert'
    return
  }
  if (selectionMode === 'invert') {
    rows.value.forEach((row) => tableRef.value?.toggleRowSelection(row, !selectedIds.value.includes(row.id)))
    selectionMode = 'clear'
    return
  }
  tableRef.value.clearSelection()
  selectionMode = 'select'
}

async function syncCurrent() {
  if (filters.accountId <= 0) {
    ElMessage.info('请先选择一个账号')
    return
  }
  const result = props.kind === 'channel' ? await panelApi.syncChannels(filters.accountId) : await panelApi.syncGroups(filters.accountId)
  ElMessage.success(result.message)
  await load()
}

async function syncAll() {
  const result = props.kind === 'channel' ? await panelApi.syncChannels(null) : await panelApi.syncGroups(null)
  ElMessage.success(result.message)
  await load()
}

async function copyLink(row: Row) {
  const result = props.kind === 'channel' ? await panelApi.exportChannelLink(row.id) : await panelApi.exportGroupLink(row.id)
  await writeClipboardText(result.link)
  ElMessage.success('已复制链接')
}

async function batchCopyLinks() {
  if (selectedRows.value.length === 0) {
    ElMessage.info(`请先选择${kindName.value}`)
    return
  }
  await ElMessageBox.confirm(`将为 ${selectedRows.value.length} 个${kindName.value}生成${linkName.value}并复制到剪贴板。是否继续？`, '确认复制', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })

  loading.value = true
  const lines: string[] = []
  let ok = 0
  let skipped = 0
  let failed = 0
  try {
    lines.push(`# ExportedAtUtc: ${new Date().toISOString()}`, '# Format: <TelegramId>\\t<Title>\\t<Link>', '')
    for (const row of selectedRows.value) {
      try {
        const result = props.kind === 'channel' ? await panelApi.exportChannelLink(row.id) : await panelApi.exportGroupLink(row.id)
        ok += 1
        lines.push(`${row.telegramId}\t${row.title}\t${result.link}`)
      } catch (error) {
        const message = extractErrorMessage(error)
        if (message.includes('暂无可用执行账号')) {
          skipped += 1
          lines.push(`${row.telegramId}\t${row.title}\t(无可用执行账号)`)
        } else {
          failed += 1
          lines.push(`${row.telegramId}\t${row.title}\t(无法生成/不可见/无权限)`)
        }
      }
    }
    await writeClipboardText(lines.join('\n'))
    showOperationSummary(`已复制${linkName.value}`, ok, failed, skipped)
  } finally {
    loading.value = false
  }
}

async function batchExportInvites() {
  if (selectedRows.value.length === 0) {
    ElMessage.info(`请先选择${kindName.value}`)
    return
  }
  await ElMessageBox.confirm(`将为 ${selectedRows.value.length} 个${kindName.value}生成${linkName.value}并导出。是否继续？`, '确认导出', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })
  const ids = selectedRows.value.map((x) => x.telegramId).join(',')
  const accountId = filters.accountId > 0 ? `&accountId=${filters.accountId}` : ''
  const path = props.kind === 'channel' ? '/downloads/channels/invites.txt' : '/downloads/groups/invites.txt'
  window.location.href = `${path}?ids=${encodeURIComponent(ids)}${accountId}`
}

async function showDetails(row: Row) {
  const resourceId = row.id
  detail.row = row
  detail.admins = []
  detail.accounts = row.accounts.slice()
  detail.adminsLoading = false
  detail.accountsLoading = false
  detail.kickAccountId = filters.accountId > 0 ? filters.accountId : 0
  detail.kickTarget = ''
  detail.kickPermanent = false
  detail.visible = true
  detail.loading = true
  try {
    if (props.kind === 'channel') {
      const payload = await panelApi.channelDetail(resourceId)
      if (detail.row?.id !== resourceId) return
      detail.row = payload.channel
      detail.accounts = payload.accounts
    } else {
      const payload = await panelApi.groupDetail(resourceId)
      if (detail.row?.id !== resourceId) return
      detail.row = payload.group
      detail.accounts = payload.accounts
    }
  } finally {
    if (detail.row?.id === resourceId) detail.loading = false
  }
  await loadDetailAdmins(resourceId)
}

function setSystemAccounts(items: ChatMembershipAccount[]) {
  systemAccounts.accounts = items
    .slice()
    .sort((a, b) => Number(b.isCreator) - Number(a.isCreator) || Number(b.isAdmin) - Number(a.isAdmin) || a.accountId - b.accountId)
}

function showSystemAccounts(row: Row) {
  setSystemAccounts(row.accounts)
  systemAccounts.visible = true
}

function showDetailSystemAccounts() {
  setSystemAccounts(detail.accounts)
  systemAccounts.visible = true
}

async function loadDetailAdmins(expectedResourceId?: number) {
  const resourceId = expectedResourceId ?? detail.row?.id
  if (!resourceId) return
  detail.adminsLoading = true
  try {
    const admins = props.kind === 'channel'
      ? await panelApi.channelAdmins(resourceId)
      : await panelApi.groupAdmins(resourceId)
    if (detail.row?.id === resourceId) detail.admins = admins
  } finally {
    if (detail.row?.id === resourceId) detail.adminsLoading = false
  }
}

async function loadDetailAccounts(expectedResourceId?: number) {
  const resourceId = expectedResourceId ?? detail.row?.id
  if (!resourceId) return
  detail.accountsLoading = true
  try {
    if (props.kind === 'channel') {
      const payload = await panelApi.channelDetail(resourceId)
      if (detail.row?.id !== resourceId) return
      detail.row = payload.channel
      detail.accounts = payload.accounts
    } else {
      const payload = await panelApi.groupDetail(resourceId)
      if (detail.row?.id !== resourceId) return
      detail.row = payload.group
      detail.accounts = payload.accounts
    }
  } finally {
    if (detail.row?.id === resourceId) detail.accountsLoading = false
  }
}

function openEditFromDetail() {
  if (!detail.row) return
  openEdit(detail.row)
}

async function kickFromDetail() {
  if (!detail.row) return
  if (!detail.kickTarget.trim()) {
    ElMessage.warning('请填写用户名或用户 ID')
    return
  }
  detail.kickLoading = true
  try {
    const payload = {
      ids: [detail.row.id],
      target: detail.kickTarget.trim(),
      accountId: detail.kickAccountId || null,
      permanentBan: detail.kickPermanent,
    }
    const result = props.kind === 'channel'
      ? await panelApi.batchKickChannelUsers(payload)
      : await panelApi.batchKickGroupUsers(payload)
    detail.kickTarget = ''
    showBatchResult(detail.kickPermanent ? '封禁完成' : '踢出完成', result)
  } finally {
    detail.kickLoading = false
  }
}

async function kickAdminFromDetail(admin: ChatAdmin) {
  const groupId = detail.row?.id
  if (!groupId || props.kind !== 'group' || admin.isCreator || detail.adminKickLoadingGroupId !== null) return

  try {
    await ElMessageBox.confirm(
      `确定踢出管理员「${admin.displayName}」吗？系统会先撤销管理员权限，再将其移出群组。`,
      '确认踢出管理员',
      { type: 'warning', confirmButtonText: '踢出', cancelButtonText: '取消' },
    )
  } catch {
    return
  }

  if (detail.row?.id !== groupId || detail.adminKickLoadingGroupId !== null) return

  detail.adminKickLoadingGroupId = groupId
  detail.adminKickLoadingUserId = admin.userId
  try {
    const result = await panelApi.kickGroupAdmin(groupId, admin.userId)
    ElMessage.success(result.message || '已踢出管理员')
  } finally {
    if (detail.adminKickLoadingGroupId === groupId && detail.adminKickLoadingUserId === admin.userId) {
      detail.adminKickLoadingGroupId = null
      detail.adminKickLoadingUserId = null
    }
    await Promise.allSettled([
      loadDetailAdmins(groupId),
      loadDetailAccounts(groupId),
    ])
  }
}

function openEdit(row: Row) {
  edit.row = row
  edit.form.title = row.title
  edit.form.about = row.about || ''
  edit.form.isPublic = !!row.username
  edit.form.username = row.username || ''
  edit.form.categoryId = rowCategoryId(row) || 0
  edit.form.changeForwardingAllowed = false
  edit.form.forwardingAllowed = true
  edit.files = []
  edit.visible = true
}

async function saveEdit() {
  if (!edit.row) return
  const form = new FormData()
  form.append('title', edit.form.title)
  form.append('about', edit.form.about)
  form.append('isPublic', String(edit.form.isPublic))
  form.append('username', edit.form.username)
  form.append('categoryId', String(edit.form.categoryId || 0))
  if (props.kind === 'channel' && edit.form.changeForwardingAllowed) form.append('forwardingAllowed', String(edit.form.forwardingAllowed))
  const file = edit.files[0]?.raw
  if (file) form.append('photo', file)
  edit.saving = true
  try {
    if (props.kind === 'channel') await panelApi.updateChannel(edit.row.id, form)
    else await panelApi.updateGroup(edit.row.id, form)
    edit.visible = false
    ElMessage.success('已保存')
    await loadMeta()
    await load()
  } finally {
    edit.saving = false
  }
}

function openSingleCategory(row: Row) {
  categoryDialog.ids = [row.id]
  categoryDialog.categoryId = rowCategoryId(row) || 0
  categoryDialog.visible = true
}

function openBatchCategory() {
  categoryDialog.ids = selectedIds.value
  categoryDialog.categoryId = 0
  categoryDialog.visible = true
}

async function saveCategoryChange() {
  const value = categoryDialog.categoryId > 0 ? categoryDialog.categoryId : null
  if (props.kind === 'channel') await panelApi.batchSetChannelGroup(categoryDialog.ids, value)
  else await panelApi.batchSetGroupCategory(categoryDialog.ids, value)
  categoryDialog.visible = false
  ElMessage.success('分类已更新')
  await loadMeta()
  await load()
}

async function openTransferOwner(row: Row) {
  transferOwner.row = row
  transferOwner.accountId = row.creatorAccountId || (filters.accountId > 0 ? filters.accountId : 0)
  transferOwner.targetAccountId = null
  transferOwner.target = ''
  transferOwner.password = ''
  transferOwner.visible = true
  if (transferOwner.accountId > 0) {
    await fillTransferPasswordFromStoredAccount(transferOwner.accountId)
  }
}

async function fillTransferPasswordFromStoredAccount(accountId: number) {
  if (accountId <= 0) return
  transferOwner.passwordLoading = true
  try {
    const account = await panelApi.account(accountId)
    transferOwner.password = account.twoFactorPassword || ''
  } catch {
    transferOwner.password = ''
  } finally {
    transferOwner.passwordLoading = false
  }
}

async function onTransferExecutorChanged(value: number) {
  transferOwner.accountId = value || 0
  await fillTransferPasswordFromStoredAccount(transferOwner.accountId)
}

function onTransferTargetAccountChanged(value: number | null) {
  transferOwner.targetAccountId = value || null
  if (!transferOwner.targetAccountId) return

  const account = accounts.value.find((x) => x.id === transferOwner.targetAccountId)
  if (!account?.username) {
    ElMessage.warning('该账号没有用户名，无法作为转让目标自动填写')
    return
  }

  transferOwner.target = `@${account.username}`
}

async function submitTransferOwner() {
  if (!transferOwner.row) return
  if (transferOwner.accountId <= 0) {
    ElMessage.warning('请选择当前创建者账号')
    return
  }
  const target = transferOwner.target.trim()
  if (!target) {
    ElMessage.warning('请填写新所有者用户名')
    return
  }

  await ElMessageBox.confirm(
    `确定把${kindName.value}「${transferOwner.row.title}」转让给 ${target} 吗？成功后当前账号将不再是创建者。`,
    '确认转让所有权',
    { type: 'error', confirmButtonText: '确认转让', cancelButtonText: '取消' },
  )

  transferOwner.running = true
  try {
    const payload = {
      target,
      password: transferOwner.password,
      accountId: transferOwner.accountId,
      targetAccountId: transferOwner.targetAccountId,
    }
    const result = props.kind === 'channel'
      ? await panelApi.transferChannelOwner(transferOwner.row.id, payload)
      : await panelApi.transferGroupOwner(transferOwner.row.id, payload)
    transferOwner.visible = false
    ElMessage.success(result.message || '已转让所有权')
    await load()
  } finally {
    transferOwner.running = false
  }
}

async function leaveOne(row: Row) {
  await ElMessageBox.confirm(`确定退出${kindName.value}「${row.title}」吗？`, '确认退出', { type: 'warning' })
  if (props.kind === 'channel') await panelApi.leaveChannel(row.id)
  else await panelApi.leaveGroup(row.id)
  ElMessage.success('已退出')
  await load()
}

async function disbandOne(row: Row) {
  await confirmAdminPassword(
    `确定要解散${kindName.value}「${row.title}」吗？此操作会在 Telegram 侧删除${kindName.value}，并从本地面板移除记录。`,
    '确认解散',
  )
  if (props.kind === 'channel') await panelApi.disbandChannel(row.id)
  else await panelApi.disbandGroup(row.id)
  ElMessage.success('已解散')
  await load()
}

async function deleteOne(row: Row) {
  await ElMessageBox.confirm(`确定删除${kindName.value}「${row.title}」吗？`, '确认删除', { type: 'warning' })
  if (props.kind === 'channel') await panelApi.deleteChannel(row.id)
  else await panelApi.deleteGroup(row.id)
  ElMessage.success('已删除')
  await load()
}

async function batchLeave() {
  const targets = [...selectedRows.value]
  if (targets.length === 0) {
    ElMessage.info(`请先选择${kindName.value}`)
    return
  }
  await ElMessageBox.confirm(
    `确定要让执行账号退出选中的 ${targets.length} 个${kindName.value}吗？若系统内其他账号仍在这些${kindName.value}中，记录可能继续显示。`,
    '确认批量退出',
    { type: 'warning', confirmButtonText: '退出', cancelButtonText: '取消' },
  )
  loading.value = true
  try {
    const result = await runBatchRows(targets, async (row) => {
      if (props.kind === 'channel') await panelApi.leaveChannel(row.id)
      else await panelApi.leaveGroup(row.id)
    })
    await load()
    showBatchFailureSummary(`批量退出${kindName.value}完成`, result.success, result.failures)
  } finally {
    loading.value = false
  }
}

async function batchDisband() {
  const targets = [...selectedRows.value]
  if (targets.length === 0) {
    ElMessage.info(`请先选择${kindName.value}`)
    return
  }
  await confirmAdminPassword(
    `确定要解散选中的 ${targets.length} 个${kindName.value}吗？此操作会在 Telegram 侧删除${kindName.value}，并从本地面板移除记录。`,
    '确认批量解散',
  )
  loading.value = true
  try {
    const result = await runBatchRows(targets, async (row) => {
      if (props.kind === 'channel') await panelApi.disbandChannel(row.id)
      else await panelApi.disbandGroup(row.id)
    })
    await load()
    showBatchFailureSummary(`批量解散${kindName.value}完成`, result.success, result.failures)
  } finally {
    loading.value = false
  }
}

async function batchDelete() {
  await ElMessageBox.confirm(`确定删除已选 ${selectedIds.value.length} 个${kindName.value}吗？`, '确认删除', { type: 'warning' })
  if (props.kind === 'channel') await panelApi.batchDeleteChannels(selectedIds.value)
  else await panelApi.batchDeleteGroups(selectedIds.value)
  ElMessage.success('已删除')
  await load()
}

function parseLines(text: string) {
  return text
    .split(/\r?\n|,|;|\s+/)
    .map((x) => x.trim())
    .filter(Boolean)
}

async function loadChannelPresets() {
  const [invite, admin] = await Promise.all([panelApi.channelInvitePresets(), panelApi.channelAdminPresets()])
  invitePresets.value = invite
  adminPresets.value = admin
}

async function loadChannelAdminDefaults() {
  const defaults = await panelApi.channelAdminDefaults()
  applyChannelAdminRightsMask(defaults.rights)
}

function applyInvitePreset() {
  const preset = invitePresets.value.find((x) => x.name === channelInvite.presetName)
  if (preset) channelInvite.usernamesText = preset.values.map((x) => `@${x}`).join('\n')
}

async function saveInvitePreset() {
  const values = parseLines(channelInvite.usernamesText)
  if (values.length === 0) {
    ElMessage.warning('当前用户名列表为空，无法保存为预设')
    return
  }
  await panelApi.saveChannelInvitePreset(channelInvite.presetNameToSave.trim(), values)
  channelInvite.presetNameToSave = ''
  await loadChannelPresets()
  ElMessage.success('已保存预设')
}

async function deleteInvitePreset() {
  if (!channelInvite.presetName) return
  await ElMessageBox.confirm(`确定删除预设“${channelInvite.presetName}”吗？`, '删除预设', {
    type: 'warning',
    confirmButtonText: '删除',
    cancelButtonText: '取消',
  })
  await panelApi.deleteChannelInvitePreset(channelInvite.presetName)
  channelInvite.presetName = ''
  await loadChannelPresets()
  ElMessage.success('已删除预设')
}

function applyAdminPreset() {
  const preset = adminPresets.value.find((x) => x.name === channelAdmins.presetName)
  if (preset) channelAdmins.usernamesText = preset.values.map((x) => `@${x}`).join('\n')
}

async function saveAdminPreset() {
  const values = parseLines(channelAdmins.usernamesText)
  if (values.length === 0) {
    ElMessage.warning('当前用户名列表为空，无法保存为预设')
    return
  }
  await panelApi.saveChannelAdminPreset(channelAdmins.presetNameToSave.trim(), values)
  channelAdmins.presetNameToSave = ''
  await loadChannelPresets()
  ElMessage.success('已保存预设')
}

async function deleteAdminPreset() {
  if (!channelAdmins.presetName) return
  await ElMessageBox.confirm(`确定删除预设“${channelAdmins.presetName}”吗？`, '删除预设', {
    type: 'warning',
    confirmButtonText: '删除',
    cancelButtonText: '取消',
  })
  await panelApi.deleteChannelAdminPreset(channelAdmins.presetName)
  channelAdmins.presetName = ''
  await loadChannelPresets()
  ElMessage.success('已删除预设')
}

function openChatInvite(ids: number[]) {
  channelInvite.ids = ids
  channelInvite.accountMode = filters.accountId > 0 ? 'account' : 'auto'
  channelInvite.accountId = filters.accountId > 0 ? filters.accountId : 0
  channelInvite.accountCategoryId = accountCategoryOptions.value[0]?.id || 0
  channelInvite.presetName = ''
  channelInvite.presetNameToSave = ''
  channelInvite.usernamesText = ''
  channelInvite.delayMs = 2000
  channelInvite.visible = true
}

async function submitChannelInvite() {
  const usernames = parseLines(channelInvite.usernamesText)
  if (usernames.length === 0) {
    ElMessage.warning('请填写用户名')
    return
  }
  if (channelInvite.accountMode === 'account' && channelInvite.accountId <= 0) {
    ElMessage.warning('请选择执行账号')
    return
  }
  if (channelInvite.accountMode === 'category' && channelInvite.accountCategoryId <= 0) {
    ElMessage.warning('请选择账号分组')
    return
  }
  channelInvite.running = true
  try {
    const payload = {
      ids: channelInvite.ids,
      usernames,
      accountId: channelInvite.accountMode === 'account' ? channelInvite.accountId : null,
      accountCategoryId: channelInvite.accountMode === 'category' ? channelInvite.accountCategoryId : null,
      delayMs: channelInvite.delayMs,
    }
    const result = props.kind === 'channel'
      ? await panelApi.batchInviteChannels(payload)
      : await panelApi.batchInviteGroups(payload)
    channelInvite.visible = false
    showTaskCreated('批量邀请任务已创建', result)
  } finally {
    channelInvite.running = false
  }
}

function openChatAdmins(ids: number[]) {
  channelAdmins.ids = ids
  channelAdmins.accountId = filters.accountId > 0 ? filters.accountId : 0
  channelAdmins.presetName = ''
  channelAdmins.presetNameToSave = ''
  channelAdmins.usernamesText = ''
  channelAdmins.adminTitle = 'Admin'
  channelAdmins.delayMs = 1500
  loadChannelAdminDefaults().catch(() => applyBasicChannelAdminRights())
  channelAdmins.visible = true
}

function applyBasicChannelAdminRights() {
  applyChannelAdminRightsMask(1 | 2 | 4 | 8 | 16 | 32 | 64)
}

function applyFullChannelAdminRights() {
  applyChannelAdminRightsMask(1 | 2 | 4 | 8 | 16 | 32 | 64 | 128 | 256 | 512 | 1024)
}

function clearChannelAdminRights() {
  applyChannelAdminRightsMask(0)
}

function applyChannelAdminRightsMask(value: number) {
  Object.assign(channelAdmins.rights, {
    changeInfo: (value & 1) !== 0,
    postMessages: (value & 2) !== 0,
    editMessages: (value & 4) !== 0,
    deleteMessages: (value & 8) !== 0,
    banUsers: (value & 16) !== 0,
    inviteUsers: (value & 32) !== 0,
    pinMessages: (value & 64) !== 0,
    manageCall: (value & 128) !== 0,
    addAdmins: (value & 256) !== 0,
    anonymous: (value & 512) !== 0,
    manageTopics: (value & 1024) !== 0,
  })
}

async function saveChannelAdminDefaultRights() {
  await panelApi.saveChannelAdminDefaults(channelAdminRightsValue())
  ElMessage.success('已保存为默认权限')
}

function channelAdminRightsValue() {
  let value = 0
  if (channelAdmins.rights.changeInfo) value |= 1
  if (channelAdmins.rights.postMessages) value |= 2
  if (channelAdmins.rights.editMessages) value |= 4
  if (channelAdmins.rights.deleteMessages) value |= 8
  if (channelAdmins.rights.banUsers) value |= 16
  if (channelAdmins.rights.inviteUsers) value |= 32
  if (channelAdmins.rights.pinMessages) value |= 64
  if (channelAdmins.rights.manageCall) value |= 128
  if (channelAdmins.rights.addAdmins) value |= 256
  if (channelAdmins.rights.anonymous) value |= 512
  if (channelAdmins.rights.manageTopics) value |= 1024
  return value
}

async function submitChannelAdmins() {
  const usernames = parseLines(channelAdmins.usernamesText)
  if (usernames.length === 0) {
    ElMessage.warning('请填写管理员用户名')
    return
  }
  channelAdmins.running = true
  try {
    const payload = {
      ids: channelAdmins.ids,
      usernames,
      accountId: channelAdmins.accountId || null,
      rights: channelAdminRightsValue(),
      adminTitle: channelAdmins.adminTitle.trim() || 'Admin',
      delayMs: channelAdmins.delayMs,
    }
    const result = props.kind === 'channel'
      ? await panelApi.batchSetChannelAdmins(payload)
      : await panelApi.batchSetGroupAdmins(payload)
    channelAdmins.visible = false
    showBatchResult('设置管理员完成', result)
  } finally {
    channelAdmins.running = false
  }
}

function openChatKick(ids: number[]) {
  channelKick.ids = ids
  channelKick.accountId = filters.accountId > 0 ? filters.accountId : 0
  channelKick.target = ''
  channelKick.permanentBan = false
  channelKick.visible = true
}

async function submitChannelKick() {
  if (!channelKick.target.trim()) {
    ElMessage.warning('请填写用户名或用户 ID')
    return
  }
  channelKick.running = true
  try {
    const payload = {
      ids: channelKick.ids,
      target: channelKick.target.trim(),
      accountId: channelKick.accountId || null,
      permanentBan: channelKick.permanentBan,
    }
    const result = props.kind === 'channel'
      ? await panelApi.batchKickChannelUsers(payload)
      : await panelApi.batchKickGroupUsers(payload)
    channelKick.visible = false
    showBatchResult(channelKick.permanentBan ? '封禁完成' : '踢出完成', result)
  } finally {
    channelKick.running = false
  }
}

function showBatchResult(title: string, result: { success: number; failed: number; items: Array<{ phone?: string | null; success: boolean; summary: string; error?: string | null }> }) {
  const summary = `成功 ${result.success}，失败 ${result.failed}`
  if (result.failed === 0) {
    ElMessage.success(`${title}：${summary}`)
    return
  }
  const details = result.items
    .filter((x) => !x.success)
    .slice(0, 20)
    .map((x) => `${x.phone || '-'}：${x.error || x.summary}`)
    .join('\n')
  ElMessageBox.alert(details || summary, `${title}：${summary}`, { type: 'warning' })
}

function showTaskCreated(title: string, task: { id: number; total: number }) {
  ElMessageBox.confirm(
    `任务 #${task.id} 已加入任务中心，共 ${task.total} 次邀请操作。可以在任务中心手动暂停或取消。`,
    title,
    {
      type: 'success',
      confirmButtonText: '查看任务中心',
      cancelButtonText: '留在当前页',
    },
  )
    .then(() => router.push('/tasks'))
    .catch(() => undefined)
}

async function confirmAdminPassword(prompt: string, confirmText: string) {
  const password = await ElMessageBox.prompt(prompt, '后台密码确认', {
    type: 'warning',
    inputType: 'password',
    inputPlaceholder: '后台密码',
    confirmButtonText: confirmText,
    cancelButtonText: '取消',
    inputValidator: (value) => !!String(value || '').trim() || '请输入后台密码',
  })

  const result = await panelApi.verifyAdminPassword(String(password.value || ''))
  if (!result.success) {
    throw new Error(result.message || '后台密码错误')
  }
}

async function runBatchRows(targets: Row[], action: (row: Row) => Promise<void>) {
  let success = 0
  const failures: string[] = []
  for (const row of targets) {
    try {
      await action(row)
      success += 1
    } catch (error) {
      failures.push(`${row.title}：${extractErrorMessage(error)}`)
    }
  }
  return { success, failures }
}

function showBatchFailureSummary(title: string, success: number, failures: string[]) {
  const summary = `成功 ${success} 个，失败 ${failures.length} 个`
  if (failures.length === 0) {
    ElMessage.success(`${title}：${summary}`)
    return
  }
  const details = failures.slice(0, 80).join('\n')
  ElMessageBox.alert(details, `${title}：${summary}`, { type: success > 0 ? 'warning' : 'error' })
}

function showOperationSummary(title: string, ok: number, failed: number, skipped: number) {
  const text = `${title}：成功 ${ok}，失败 ${failed}，跳过 ${skipped}`
  if (failed === 0) ElMessage.success(text)
  else ElMessage.warning(text)
}

function extractErrorMessage(error: unknown) {
  const anyError = error as { response?: { data?: { message?: string; title?: string } }; message?: string }
  return anyError?.response?.data?.message || anyError?.response?.data?.title || anyError?.message || '操作失败'
}

function readPositiveQueryNumber(value: unknown) {
  const raw = Array.isArray(value) ? value[0] : value
  const numeric = Number(raw || 0)
  return Number.isFinite(numeric) && numeric > 0 ? numeric : 0
}

watch(
  () => route.query.accountId,
  (value) => {
    const next = readPositiveQueryNumber(value)
    if (next === filters.accountId) return
    filters.accountId = next
    reloadFirst()
  },
)

onMounted(async () => {
  await loadMeta()
  await load()
})
</script>

<style scoped>
.resource-filter {
  width: 240px;
}

.autofill-decoy {
  position: fixed;
  top: -10000px;
  left: -10000px;
  width: 1px;
  height: 1px;
  overflow: hidden;
  opacity: 0;
  pointer-events: none;
}

.autofill-decoy input {
  width: 1px;
  height: 1px;
  border: 0;
  padding: 0;
}

.toolbar-spacer {
  flex: 1;
}

.full {
  width: 100%;
}

.row-actions {
  display: flex;
  align-items: center;
  gap: 2px;
}

.rights-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(120px, 1fr));
  gap: 6px 12px;
  width: 100%;
}

.mt-2 {
  margin-top: 8px;
}
</style>
