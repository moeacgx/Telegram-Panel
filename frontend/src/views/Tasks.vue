<template>
  <div>
    <el-card shadow="never" class="page-card">
      <div class="toolbar task-toolbar">
        <div class="toolbar-title">总任务数 ({{ tasks.length }})</div>
        <div class="toolbar-spacer" />
        <el-button :icon="Refresh" :loading="loading" @click="load()">刷新</el-button>
        <el-button type="primary" :icon="CirclePlus" :disabled="loading" @click="openCreateDialog">新建任务</el-button>
        <el-button type="danger" plain :icon="Delete" :disabled="loading || tasks.length === 0" @click="openCleanupDialog">
          清理全部任务
        </el-button>
        <el-select v-model="taskLoadCount" class="filter" placeholder="加载数量" @change="load()">
          <el-option label="最近 50 条" :value="50" />
          <el-option label="最近 100 条" :value="100" />
          <el-option label="最近 200 条" :value="200" />
          <el-option label="最近 500 条" :value="500" />
        </el-select>
        <el-select v-model="filterCategory" class="filter" placeholder="任务分类">
          <el-option label="全部" value="all" />
          <el-option v-for="category in availableCategories" :key="category" :label="categoryName(category)" :value="category" />
        </el-select>
        <el-select v-model="historyStatusFilter" class="filter" placeholder="历史状态">
          <el-option label="全部" value="all" />
          <el-option label="已完成" value="completed" />
          <el-option label="失败" value="failed" />
          <el-option label="已取消" value="canceled" />
        </el-select>
      </div>
    </el-card>

    <el-card shadow="never" class="page-card mt-4">
      <template #header><span>计划任务 ({{ scheduledTasks.length }})</span></template>
      <el-table v-loading="loading && !hasLoaded" :data="scheduledTasks" stripe row-key="id">
        <el-table-column prop="id" label="ID" width="72" />
        <el-table-column label="任务名称" min-width="180">
          <template #default="{ row }">{{ scheduledName(row) }}</template>
        </el-table-column>
        <el-table-column label="归属" width="110">
          <template #default="{ row }"><el-tag size="small">{{ categoryName(taskCategory(row.taskType)) }}</el-tag></template>
        </el-table-column>
        <el-table-column label="任务类型" min-width="240">
          <template #default="{ row }">{{ taskName(row.taskType) }}</template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }"><StatusTag :status="scheduledStatus(row.status)" /></template>
        </el-table-column>
        <el-table-column label="Cron" width="180">
          <template #default="{ row }">
            <div>{{ row.cronExpression }}</div>
            <div class="cell-sub">时区：{{ timeZoneId || 'UTC' }}</div>
          </template>
        </el-table-column>
        <el-table-column label="下次运行" width="180">
          <template #default="{ row }">{{ formatTime(row.nextRunAtUtc) || '-' }}</template>
        </el-table-column>
        <el-table-column label="上次运行" width="180">
          <template #default="{ row }">{{ formatTime(row.lastRunAtUtc) || '-' }}</template>
        </el-table-column>
        <el-table-column label="操作" width="112" fixed="right">
          <template #default="{ row }">
            <el-dropdown trigger="click" placement="bottom-end" class="task-action-dropdown">
              <el-button size="small" :icon="MoreFilled" aria-label="操作">操作</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item :icon="InfoFilled" @click="showScheduledDetails(row)">详情</el-dropdown-item>
                  <el-dropdown-item v-if="isDirectMessagingTask(row.taskType)" disabled>私信任务不支持 Cron 计划</el-dropdown-item>
                  <el-dropdown-item v-if="!isDirectMessagingTask(row.taskType)" :icon="VideoPlay" @click="runScheduledNow(row)">立即执行</el-dropdown-item>
                  <el-dropdown-item v-if="!isDirectMessagingTask(row.taskType)" :icon="Edit" @click="openEditScheduled(row)">编辑</el-dropdown-item>
                  <el-dropdown-item v-if="!isDirectMessagingTask(row.taskType) && canCopyScheduled(row)" :icon="CopyDocument" @click="copyScheduledTask(row)">复制</el-dropdown-item>
                  <el-dropdown-item v-if="row.status === 'enabled'" :icon="VideoPause" @click="pauseScheduled(row)">暂停</el-dropdown-item>
                  <el-dropdown-item v-else :icon="VideoPlay" @click="resumeScheduled(row.id)">恢复</el-dropdown-item>
                  <el-dropdown-item divided :icon="Delete" @click="deleteScheduled(row)">删除</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty description="暂无计划任务" />
        </template>
      </el-table>
    </el-card>

    <el-card shadow="never" class="page-card mt-4">
      <template #header><span>执行中任务 ({{ activeTasks.length }})</span></template>
      <el-table v-loading="loading && !hasLoaded" :data="activeTasks" stripe row-key="id">
        <el-table-column prop="id" label="任务ID" width="82" />
        <el-table-column label="归属" width="110">
          <template #default="{ row }"><el-tag size="small">{{ categoryName(taskCategory(row.taskType)) }}</el-tag></template>
        </el-table-column>
        <el-table-column label="任务名称" min-width="260">
          <template #default="{ row }">
            <div>{{ batchTaskName(row) }}</div>
            <div class="cell-sub">类型：{{ taskName(row.taskType) }}</div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <StatusTag :status="displayStatus(row)" />
            <el-tag v-if="isPersistentTask(row)" size="small" type="info" class="mt-2">常驻</el-tag>
            <el-tag v-if="row.requiresAttention" size="small" type="danger" class="mt-2">需处理</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="进度" min-width="220">
          <template #default="{ row }">
            <template v-if="isPersistentTask(row)">
              <el-progress :percentage="100" :stroke-width="8" status="success" :show-text="false" />
              <div class="cell-sub">常驻运行，已发送 {{ row.completed }} 条，失败 {{ row.failed }}</div>
              <div v-if="row.runtimeMessage" class="cell-sub">{{ row.runtimeMessage }}</div>
            </template>
            <template v-else>
              <el-progress :percentage="taskProgress(row)" :stroke-width="8" />
              <div class="cell-sub">{{ row.completed }} / {{ row.total }}，失败 {{ row.failed }}</div>
            </template>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="180">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="完成时间" width="180">
          <template #default="{ row }">{{ formatTime(row.completedAt) || '-' }}</template>
        </el-table-column>
        <el-table-column label="操作" width="112" fixed="right">
          <template #default="{ row }">
            <el-dropdown trigger="click" placement="bottom-end" class="task-action-dropdown">
              <el-button size="small" :icon="MoreFilled" aria-label="操作">操作</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item :icon="InfoFilled" @click="showTaskDetails(row)">详情</el-dropdown-item>
                  <el-dropdown-item v-if="canPause(row)" :icon="VideoPause" @click="pauseTask(row.id)">暂停</el-dropdown-item>
                  <el-dropdown-item v-if="canResume(row)" :icon="VideoPlay" @click="resumeTask(row.id)">恢复</el-dropdown-item>
                  <el-dropdown-item v-if="canEdit(row)" :icon="Edit" @click="openEditTask(row)">编辑</el-dropdown-item>
                  <el-dropdown-item v-if="canCopyTask(row)" :icon="CopyDocument" @click="copyTask(row)">复制</el-dropdown-item>
                  <el-dropdown-item v-if="canCancel(row)" :icon="CircleCloseFilled" @click="cancelTask(row.id)">取消</el-dropdown-item>
                  <el-dropdown-item divided :icon="Delete" @click="deleteTask(row)">删除</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty description="暂无执行中任务" />
        </template>
      </el-table>
    </el-card>

    <el-card shadow="never" class="page-card mt-4">
      <template #header>
        <div class="card-header">
          <span>历史任务 ({{ historyTasks.length }})</span>
          <span class="cell-sub">当前已加载 {{ tasks.length }} 条任务记录</span>
        </div>
      </template>
      <el-table v-loading="loading && !hasLoaded" :data="pagedHistoryTasks" stripe row-key="id">
        <el-table-column prop="id" label="任务ID" width="82" />
        <el-table-column label="归属" width="110">
          <template #default="{ row }"><el-tag size="small">{{ categoryName(taskCategory(row.taskType)) }}</el-tag></template>
        </el-table-column>
        <el-table-column label="任务名称" min-width="260">
          <template #default="{ row }">
            <div>{{ batchTaskName(row) }}</div>
            <div class="cell-sub">类型：{{ taskName(row.taskType) }}</div>
          </template>
        </el-table-column>
        <el-table-column label="状态" width="100">
          <template #default="{ row }">
            <StatusTag :status="displayStatus(row)" />
            <el-tag v-if="isPersistentTask(row)" size="small" type="info" class="mt-2">常驻</el-tag>
            <el-tag v-if="row.requiresAttention" size="small" type="danger" class="mt-2">需处理</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="进度" min-width="220">
          <template #default="{ row }">
            <template v-if="isPersistentTask(row)">
              <el-progress :percentage="100" :stroke-width="8" status="success" :show-text="false" />
              <div class="cell-sub">常驻任务，已发送 {{ row.completed }} 条，失败 {{ row.failed }}</div>
            </template>
            <template v-else>
              <el-progress :percentage="taskProgress(row)" :stroke-width="8" />
              <div class="cell-sub">{{ row.completed }} / {{ row.total }}，失败 {{ row.failed }}</div>
            </template>
          </template>
        </el-table-column>
        <el-table-column label="创建时间" width="180">
          <template #default="{ row }">{{ formatTime(row.createdAt) }}</template>
        </el-table-column>
        <el-table-column label="完成时间" width="180">
          <template #default="{ row }">{{ formatTime(row.completedAt) || '-' }}</template>
        </el-table-column>
        <el-table-column label="操作" width="112" fixed="right">
          <template #default="{ row }">
            <el-dropdown trigger="click" placement="bottom-end" class="task-action-dropdown">
              <el-button size="small" :icon="MoreFilled" aria-label="操作">操作</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item :icon="InfoFilled" @click="showTaskDetails(row)">详情</el-dropdown-item>
                  <el-dropdown-item v-if="canEdit(row)" :icon="Edit" @click="openEditTask(row)">编辑</el-dropdown-item>
                  <el-dropdown-item v-if="canCopyTask(row)" :icon="CopyDocument" @click="copyTask(row)">复制</el-dropdown-item>
                  <el-dropdown-item v-if="canRerun(row)" :icon="RefreshRight" @click="rerunTask(row)">重跑</el-dropdown-item>
                  <el-dropdown-item divided :icon="Delete" @click="deleteTask(row)">删除</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </template>
        </el-table-column>
        <template #empty>
          <el-empty description="暂无历史任务" />
        </template>
      </el-table>
      <div v-if="historyTasks.length > historyPageSize" class="pager">
        <el-pagination
          v-model:current-page="historyPage"
          v-model:page-size="historyPageSize"
          :page-sizes="[20, 50, 100]"
          layout="total, sizes, prev, pager, next, jumper"
          :total="historyTasks.length"
          @size-change="historyPage = 1"
        />
      </div>
    </el-card>

    <el-dialog v-model="createDialog.visible" title="新建任务" width="min(760px, calc(100vw - 24px))" destroy-on-close class="task-dialog">
      <el-alert
        title="立即执行会创建一条后台执行记录；Cron 计划会按面板时区持续调度，并自动加入随机延迟避免多个计划任务整点同时启动。"
        type="info"
        :closable="false"
        class="mb-3"
      />
      <el-form :label-position="isTaskDialogCompact ? 'top' : 'right'" :label-width="isTaskDialogCompact ? 'auto' : '96px'">
        <template v-if="createDialog.sourceTaskId > 0">
          <el-alert
            :title="`已复制 ${createDialog.sourceDescription} 的配置；确认任务名称、提交方式和配置后再提交，不会修改原任务。`"
            type="success"
            :closable="false"
            class="mb-3"
          />
          <el-row :gutter="12">
            <el-col :xs="24" :sm="12">
              <el-form-item label="来源任务">
                <el-input :model-value="createDialog.sourceDescription" disabled />
              </el-form-item>
            </el-col>
            <el-col :xs="24" :sm="12">
              <el-form-item label="任务类型">
                <el-input :model-value="taskName(createDialog.form.taskType)" disabled />
              </el-form-item>
            </el-col>
          </el-row>
        </template>
        <el-row v-else :gutter="12">
          <el-col :xs="24" :sm="12">
            <el-form-item label="任务分类">
              <el-select v-model="createDialog.form.category" class="full" @change="ensureTaskType">
                <el-option v-for="category in creatableCategories" :key="category" :label="categoryName(category)" :value="category" />
              </el-select>
            </el-form-item>
          </el-col>
          <el-col :xs="24" :sm="12">
            <el-form-item label="任务类型">
              <el-select v-model="createDialog.form.taskType" class="full" @change="onTaskTypeChanged">
                <el-option v-for="item in creatableDefinitions" :key="item.taskType" :label="item.displayName" :value="item.taskType" />
              </el-select>
            </el-form-item>
          </el-col>
        </el-row>
        <el-form-item label="提交方式">
          <el-radio-group v-model="createDialog.form.mode" @change="onCreateModeChanged">
            <el-radio-button value="once">立即执行</el-radio-button>
            <el-radio-button value="scheduled" :disabled="!currentCreateDefinition?.canSchedule || isDirectMessagingTask(createDialog.form.taskType)">Cron 计划</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-alert v-if="isDirectMessagingTask(createDialog.form.taskType) && createDialog.form.mode === 'scheduled'" title="私信任务暂不支持 Cron 计划，请使用立即执行方式。" type="warning" :closable="false" class="mb-3" />
        <el-alert v-else-if="currentCreateDefinition?.description" :title="currentCreateDefinition.description" type="info" :closable="false" class="mb-3" />
        <el-alert
          v-if="createDialog.sourceTaskId > 0"
          :title="`已复制任务 #${createDialog.sourceTaskId} 的配置；确认后会创建新任务，原任务不会被修改。`"
          type="success"
          :closable="false"
          class="mb-3"
        />
        <el-form-item v-if="!currentCreateTarget" label="任务名称">
          <el-input
            v-model="createDialog.form.name"
            maxlength="100"
            show-word-limit
            placeholder="可选，留空则显示任务类型和 ID"
          />
        </el-form-item>


        <template v-if="currentCreateTarget">
          <el-alert
            :title="`${taskName(createDialog.form.taskType)} 使用专用配置页管理，点击下方按钮进入配置。`"
            type="info"
            :closable="false"
            class="mb-3"
          />
          <el-form-item label="配置入口">
            <div class="route-actions">
              <el-button type="primary" @click="openCreateTargetWindow">打开窗口</el-button>
              <el-button @click="goCreateTarget">前往页面</el-button>
            </div>
          </el-form-item>
        </template>

        <DirectMessagingTaskConfigForm
          v-if="isDirectMessagingTask(createDialog.form.taskType)"
          :task-type="createDialog.form.taskType"
          :initial-config-json="createDialog.form.config"
          @draft-changed="onCreateDraftChanged"
        />

        <TaskConfigForm
          v-else-if="hasTaskConfigForm(createDialog.form.taskType)"
          :task-type="createDialog.form.taskType"
          :initial-config-json="createDialog.form.config"
          @draft-changed="onCreateDraftChanged"
        />

        <template v-if="!currentCreateTarget && createDialog.form.mode === 'scheduled'">
          <el-form-item label="Cron">
            <el-input v-model="createDialog.form.cronExpression" placeholder="0 9 * * *" />
          </el-form-item>
          <el-form-item label="预设">
            <el-button v-for="preset in cronPresets" :key="preset.value" size="small" @click="createDialog.form.cronExpression = preset.value">
              {{ preset.label }}
            </el-button>
          </el-form-item>
        </template>
        <el-collapse v-if="!currentCreateTarget && !hasTaskConfigForm(createDialog.form.taskType)" :model-value="createDialog.sourceTaskId > 0 ? ['json'] : []" class="mb-3">
          <el-collapse-item title="高级 JSON 配置" name="json">
            <el-alert
              title="该任务没有专用表单时，可填写通用 JSON 配置；模块后台会按任务类型解析。无需配置可留空。"
              type="info"
              :closable="false"
              class="mb-3"
            />
            <el-form-item label="任务配置">
              <el-input v-model="createDialog.form.config" type="textarea" :rows="8" placeholder="模块任务配置 JSON；若无需配置可留空" />
            </el-form-item>
          </el-collapse-item>
        </el-collapse>
        <el-form-item v-if="!currentCreateTarget && !hasTaskConfigForm(createDialog.form.taskType)" label="总数">
          <el-input-number v-model="createDialog.form.total" :min="0" :max="1000000" />
        </el-form-item>
        <el-form-item v-else-if="!currentCreateTarget" label="总数">
          <el-input-number :model-value="createDraft.total" :min="0" :max="1000000" disabled />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="createDialog.saving" @click="createDialog.visible = false">关闭</el-button>
        <el-button type="primary" :loading="createDialog.saving" :disabled="isDirectMessagingTask(createDialog.form.taskType) && !createDraft.canSubmit" @click="submitCreate">
          {{ createDialog.form.mode === 'scheduled' ? '保存计划' : '提交任务' }}
        </el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="moduleWindow.visible"
      :title="moduleWindow.title"
      width="min(1200px, 96vw)"
      top="4vh"
      destroy-on-close
      class="module-window-dialog"
    >
      <iframe v-if="moduleWindow.visible" class="module-window-frame" :src="moduleWindow.src" />
      <template #footer>
        <el-button @click="moduleWindow.visible = false">关闭窗口</el-button>
        <el-button type="primary" plain @click="goCreateTarget">前往页面</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="detailDialog.visible" :title="detailDialog.title" width="min(720px, calc(100vw - 24px))">
      <pre class="detail-pre">{{ detailDialog.content }}</pre>
      <template #footer>
        <el-button type="primary" @click="detailDialog.visible = false">关闭</el-button>
      </template>
    </el-dialog>

    <el-dialog v-model="editTaskDialog.visible" :title="`编辑任务 #${editTaskDialog.id}`" width="min(760px, calc(100vw - 24px))" destroy-on-close class="task-dialog">
      <el-alert
        title="编辑会更新当前任务配置；若任务已完成或失败，可保存后使用重跑创建新任务。"
        type="info"
        :closable="false"
        class="mb-3"
      />
      <el-form :label-position="isTaskDialogCompact ? 'top' : 'right'" :label-width="isTaskDialogCompact ? 'auto' : '96px'">
        <el-form-item label="任务类型">
          <el-input :model-value="taskName(editTaskDialog.form.taskType)" disabled />
        </el-form-item>
        <el-form-item label="任务名称">
          <el-input
            v-model="editTaskDialog.form.name"
            maxlength="100"
            show-word-limit
            placeholder="可选，留空则显示任务类型和 ID"
          />
        </el-form-item>
        <DirectMessagingTaskConfigForm
          v-if="isDirectMessagingTask(editTaskDialog.form.taskType)"
          :task-type="editTaskDialog.form.taskType"
          :initial-config-json="editTaskDialog.form.config"
          @draft-changed="onEditDraftChanged"
        />
        <TaskConfigForm
          v-else-if="hasTaskConfigForm(editTaskDialog.form.taskType)"
          :task-type="editTaskDialog.form.taskType"
          :initial-config-json="editTaskDialog.form.config"
          @draft-changed="onEditDraftChanged"
        />
        <el-form-item v-if="!hasTaskConfigForm(editTaskDialog.form.taskType)" label="总数">
          <el-input-number v-model="editTaskDialog.form.total" :min="0" :max="1000000" class="full" />
        </el-form-item>
        <el-form-item v-else label="总数">
          <el-input-number :model-value="editDraft.total" :min="0" :max="1000000" class="full" disabled />
        </el-form-item>
        <el-form-item v-if="!hasTaskConfigForm(editTaskDialog.form.taskType)" label="配置 JSON">
          <el-input v-model="editTaskDialog.form.config" type="textarea" :rows="12" placeholder="可留空；填写时必须是合法 JSON" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="editTaskDialog.saving" @click="editTaskDialog.visible = false">关闭</el-button>
        <el-button type="primary" :loading="editTaskDialog.saving" :disabled="isDirectMessagingTask(editTaskDialog.form.taskType) && !editDraft.canSubmit" @click="submitEditTask">保存配置</el-button>
      </template>
    </el-dialog>

    <el-dialog
      v-model="editScheduledDialog.visible"
      :title="'编辑计划任务：' + (editScheduledDialog.form.name || '#' + editScheduledDialog.id)"
      width="min(760px, calc(100vw - 24px))"
      destroy-on-close
      class="task-dialog"
    >
      <el-alert :title="`Cron 按面板时区解析：${timeZoneId || 'UTC'}。保存后会重新计算下次运行时间，并加入随机延迟避免整点并发。`" type="info" :closable="false" class="mb-3" />
      <el-form :label-position="isTaskDialogCompact ? 'top' : 'right'" :label-width="isTaskDialogCompact ? 'auto' : '96px'">
        <el-form-item label="任务名称">
          <el-input v-model="editScheduledDialog.form.name" maxlength="100" show-word-limit />
        </el-form-item>
        <el-form-item label="任务类型">
          <el-input :model-value="taskName(editScheduledDialog.form.taskType)" disabled />
        </el-form-item>
        <el-form-item label="Cron">
          <el-input v-model="editScheduledDialog.form.cronExpression" placeholder="0 9 * * *" />
        </el-form-item>
        <el-form-item label="预设">
          <el-button v-for="preset in cronPresets" :key="preset.value" size="small" @click="editScheduledDialog.form.cronExpression = preset.value">
            {{ preset.label }}
          </el-button>
        </el-form-item>
        <el-form-item label="状态">
          <el-radio-group v-model="editScheduledDialog.form.status">
            <el-radio-button value="enabled">启用</el-radio-button>
            <el-radio-button value="paused">暂停</el-radio-button>
          </el-radio-group>
        </el-form-item>
        <el-alert
          v-if="isDirectMessagingTask(editScheduledDialog.form.taskType)"
          title="私信任务暂不支持 Cron 计划编辑，请关闭后使用普通任务编辑。"
          type="warning"
          :closable="false"
          class="mb-3"
        />
        <TaskConfigForm
          v-else-if="!isDirectMessagingTask(editScheduledDialog.form.taskType) && hasTaskConfigForm(editScheduledDialog.form.taskType)"
          :task-type="editScheduledDialog.form.taskType"
          :initial-config-json="editScheduledDialog.form.configJson"
          @draft-changed="onEditScheduledDraftChanged"
        />
        <el-form-item v-if="!hasTaskConfigForm(editScheduledDialog.form.taskType)" label="总数">
          <el-input-number v-model="editScheduledDialog.form.total" :min="0" :max="1000000" class="full" />
        </el-form-item>
        <el-form-item v-else label="总数">
          <el-input-number :model-value="editScheduledDraft.total" :min="0" :max="1000000" class="full" disabled />
        </el-form-item>
        <el-form-item v-if="!hasTaskConfigForm(editScheduledDialog.form.taskType)" label="配置 JSON">
          <el-input v-model="editScheduledDialog.form.configJson" type="textarea" :rows="12" placeholder="可留空；填写时必须是合法 JSON" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button :disabled="editScheduledDialog.saving" @click="editScheduledDialog.visible = false">关闭</el-button>
        <el-button type="primary" :loading="editScheduledDialog.saving" :disabled="isDirectMessagingTask(editScheduledDialog.form.taskType) || (hasTaskConfigForm(editScheduledDialog.form.taskType) && !editScheduledDraft.canSubmit)" @click="submitEditScheduled">保存计划任务</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { CircleCloseFilled, CirclePlus, CopyDocument, Delete, Edit, InfoFilled, MoreFilled, Refresh, RefreshRight, VideoPause, VideoPlay } from '@element-plus/icons-vue'
import { useRouter } from 'vue-router'
import { panelApi } from '@/api/panel'
import type { BatchTask, ScheduledTask, TaskDefinition } from '@/api/types'
import StatusTag from '@/components/StatusTag.vue'
import TaskConfigForm, { type TaskConfigDraft } from '@/components/TaskConfigForm.vue'
import DirectMessagingTaskConfigForm from '@/components/DirectMessagingTaskConfigForm.vue'
import { formatTime, taskProgress } from '@/utils/format'
import { useMediaQuery } from '@/utils/useMediaQuery'

const loading = ref(false)
const router = useRouter()
const tasks = ref<BatchTask[]>([])
const scheduledTasks = ref<ScheduledTask[]>([])
const definitions = ref<TaskDefinition[]>([])
const timeZoneId = ref('')
const taskLoadCount = ref(50)
const filterCategory = ref('all')
const historyStatusFilter = ref('all')
const historyPage = ref(1)
const historyPageSize = ref(50)
const hasLoaded = ref(false)
let timer: number | undefined
let loadPromise: Promise<void> | null = null
const isTaskDialogCompact = useMediaQuery('(max-width: 640px)')

const needsAutoRefresh = computed(() =>
  tasks.value.some((task) => isActiveStatus(displayStatus(task)))
  || scheduledTasks.value.some((task) => task.status === 'enabled'),
)

const cronPresets = [
  { label: '每30分钟', value: '*/30 * * * *' },
  { label: '每小时', value: '0 * * * *' },
  { label: '每天9点', value: '0 9 * * *' },
  { label: '每天12点', value: '0 12 * * *' },
  { label: '每天21点', value: '0 21 * * *' },
]

const createDialog = ref({
  visible: false,
  saving: false,
  sourceTaskId: 0,
  sourceDescription: '',
  form: {
    category: '',
    taskType: '',
    name: '',
    mode: 'once',
    cronExpression: '0 9 * * *',
    config: '',
    total: 0,
  },
})

const moduleWindow = ref({
  visible: false,
  title: '任务配置',
  src: '',
})

const createDraft = ref(emptyDraft())

const detailDialog = ref({
  visible: false,
  title: '',
  content: '',
})

const editTaskDialog = ref({
  visible: false,
  saving: false,
  id: 0,
  form: {
    taskType: '',
    name: '',
    total: 0,
    config: '',
  },
})
const editDraft = ref(emptyDraft())

const editScheduledDialog = ref({
  visible: false,
  saving: false,
  id: 0,
  form: {
    taskType: '',
    name: '',
    total: 0,
    configJson: '',
    cronExpression: '',
    status: 'enabled',
  },
})
const editScheduledDraft = ref(emptyDraft())

const availableCategories = computed(() => {
  const set = new Set(definitions.value.map((x) => (x.category || '').trim()).filter(Boolean))
  return Array.from(set).sort()
})

const taskCenterCreateDefinitions = computed(() => definitions.value.filter((x) => x.canCreate))

const creatableCategories = computed(() => {
  const set = new Set(taskCenterCreateDefinitions.value.map((x) => (x.category || '').trim()).filter(Boolean))
  return Array.from(set).sort()
})

const creatableDefinitions = computed(() =>
  taskCenterCreateDefinitions.value
    .filter((x) => x.category === createDialog.value.form.category)
    .sort((a, b) => a.displayName.localeCompare(b.displayName, 'zh-Hans-CN')),
)

const currentCreateDefinition = computed(() => definitions.value.find((x) => x.taskType === createDialog.value.form.taskType))
const currentCreateTarget = computed(() => {
  const definition = currentCreateDefinition.value
  if (createDialog.value.sourceTaskId > 0) return ''
  if (definition && isDirectMessagingTask(definition.taskType)) return ''
  if (!definition || hasTaskConfigForm(definition.taskType)) return ''
  return resolveCreateTarget(definition)
})

const categoryFilteredTasks = computed(() =>
  tasks.value.filter((x) => filterCategory.value === 'all' || taskCategory(x.taskType) === filterCategory.value),
)

const activeTasks = computed(() =>
  categoryFilteredTasks.value
    .filter((x) => isActiveStatus(displayStatus(x)))
    .sort((a, b) => new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime()),
)

const historyTasks = computed(() =>
  categoryFilteredTasks.value
    .filter((x) => isHistoryStatus(displayStatus(x)))
    .filter((x) => historyStatusFilter.value === 'all' || displayStatus(x) === historyStatusFilter.value)
    .sort((a, b) => new Date(b.completedAt || b.createdAt).getTime() - new Date(a.completedAt || a.createdAt).getTime()),
)

const pagedHistoryTasks = computed(() => {
  const start = (historyPage.value - 1) * historyPageSize.value
  return historyTasks.value.slice(start, start + historyPageSize.value)
})

watch([filterCategory, historyStatusFilter, taskLoadCount], () => {
  historyPage.value = 1
})

watch(historyTasks, () => {
  const maxPage = Math.max(1, Math.ceil(historyTasks.value.length / historyPageSize.value))
  if (historyPage.value > maxPage) historyPage.value = maxPage
})

function taskDefinition(type: string) {
  return definitions.value.find((x) => x.taskType === type)
}

function taskName(type: string) {
  return taskDefinition(type)?.displayName || fallbackTaskName(type)
}

function scheduledName(task: ScheduledTask) {
  return task.name?.trim() || `${taskName(task.taskType)} #${task.id}`
}

function batchTaskName(task: BatchTask) {
  return task.name?.trim() || `${taskName(task.taskType)} #${task.id}`
}

function taskCategory(type: string) {
  return taskDefinition(type)?.category || (type === 'account_auto_sync' ? 'system' : 'other')
}

function categoryName(category: string) {
  if (category === 'user') return '用户任务'
  if (category === 'bot') return 'Bot 任务'
  if (category === 'system') return '系统任务'
  if (category === 'channel') return '频道任务'
  return category || '-'
}

function fallbackTaskName(type: string) {
  if (type === 'user_join_subscribe') return '批量加群/订阅/启用Bot'
  if (type === 'user_chat_active') return '账号持续活跃（群组/频道/Bot）'
  if (type === 'bot_channel_set_admins_by_account') return 'Bot频道批量设置管理员（账号执行）'
  if (type === 'bot_set_admins') return 'Bot频道批量设置管理员（机器人执行）'
  if (type === 'account_auto_sync') return '账号数据同步'
  if (type === 'auto_change_login_email') return '自动更改登录邮箱'
  return type
}

function displayStatus(task: BatchTask) {
  if (task.status === 'failed' && task.completedAt && task.total > 0 && task.completed >= task.total) return 'completed'
  return task.status
}

function isPersistentTask(task: BatchTask) {
  return task.executionKind === 'persistent'
}

function scheduledStatus(status: string) {
  return status === 'paused' ? 'paused' : 'enabled'
}

function isActiveStatus(status: string) {
  return status === 'pending' || status === 'running' || status === 'pausing' || status === 'paused'
}

function isHistoryStatus(status: string) {
  return status === 'completed' || status === 'failed' || status === 'canceled'
}

function canPause(task: BatchTask) {
  const def = taskDefinition(task.taskType)
  return def?.canPause && (task.status === 'running' || task.status === 'pending')
}

function canResume(task: BatchTask) {
  const def = taskDefinition(task.taskType)
  return def?.canResume && displayStatus(task) === 'paused'
}

function canCancel(task: BatchTask) {
  return isActiveStatus(displayStatus(task))
}

function canRerun(task: BatchTask) {
  return taskDefinition(task.taskType)?.canRerun && isHistoryStatus(displayStatus(task))
}


function canEdit(task: BatchTask) {
  const def = taskDefinition(task.taskType)
  if (!def?.canEdit) return false
  const status = displayStatus(task)
  if (status === 'paused') return true
  return (status === 'pending' || status === 'running') && def.autoPauseBeforeEdit
}

function canCopyTask(task: BatchTask) {
  return canCopyDefinition(task.taskType)
}

function canCopyScheduled(task: ScheduledTask) {
  return !isDirectMessagingTask(task.taskType) && canCopyDefinition(task.taskType)
}

function canCopyDefinition(taskType: string) {
  return !!taskType.trim() && !!taskDefinition(taskType)
}

async function load(options: { silent?: boolean } = {}) {
  const showLoading = !options.silent
  if (showLoading) loading.value = true
  if (!loadPromise) {
    loadPromise = (async () => {
      const data = await panelApi.tasks(taskLoadCount.value)
      tasks.value = data.tasks
      scheduledTasks.value = data.scheduledTasks
      definitions.value = data.definitions
      timeZoneId.value = data.timeZoneId || ''
      hasLoaded.value = true
    })().finally(() => {
      loadPromise = null
    })
  }
  try {
    await loadPromise
  } finally {
    if (showLoading) loading.value = false
  }
}

function openCreateDialog() {
  const firstCategory = creatableCategories.value[0] || 'user'
  createDialog.value.sourceTaskId = 0
  createDraft.value = emptyDraft()
  createDialog.value.sourceTaskId = 0
  createDialog.value.sourceDescription = ''
  createDialog.value.form = {
    category: firstCategory,
    taskType: '',
    name: '',
    mode: 'once',
    cronExpression: '0 9 * * *',
    config: '',
    total: 0,
  }
  ensureTaskType()
  createDialog.value.visible = true
}

function ensureTaskType() {
  const first = creatableDefinitions.value[0]
  createDialog.value.sourceTaskId = 0
  createDialog.value.sourceDescription = ''
  createDialog.value.form.taskType = first?.taskType || ''
  createDialog.value.form.name = ''
  createDialog.value.form.config = ''
  createDialog.value.form.total = defaultTotalForTask(createDialog.value.form.taskType)
  createDialog.value.form.mode = first?.canSchedule && !isDirectMessagingTask(createDialog.value.form.taskType) ? createDialog.value.form.mode : 'once'
  createDraft.value = emptyDraft()
}

function onTaskTypeChanged() {
  createDialog.value.sourceTaskId = 0
  createDialog.value.sourceDescription = ''
  createDialog.value.form.name = ''
  createDialog.value.form.config = ''
  createDialog.value.form.total = defaultTotalForTask(createDialog.value.form.taskType)
  if (!currentCreateDefinition.value?.canSchedule || isDirectMessagingTask(createDialog.value.form.taskType)) createDialog.value.form.mode = 'once'
  createDraft.value = emptyDraft()
}

function onCreateModeChanged(mode: string | number | boolean | undefined) {
  const defaultName = taskName(createDialog.value.form.taskType)
  if (mode === 'scheduled' && !createDialog.value.form.name.trim()) {
    createDialog.value.form.name = defaultName
    return
  }
  if (mode === 'once' && createDialog.value.form.name.trim() === defaultName) {
    createDialog.value.form.name = ''
  }
}

async function submitCreate() {
  const form = createDialog.value.form
  if (!form.taskType) {
    ElMessage.warning('请先选择任务类型')
    return
  }
  if (isDirectMessagingTask(form.taskType) && !createDraft.value.canSubmit) {
    ElMessage.warning(createDraft.value.validationError || '请先完善私信任务配置')
    return
  }
  if (form.mode === 'scheduled' && isDirectMessagingTask(form.taskType)) {
    ElMessage.warning('私信任务暂不支持 Cron 计划，请使用立即执行方式')
    form.mode = 'once'
    return
  }
  if (currentCreateTarget.value) {
    ElMessage.info('该任务建议使用专用配置页创建，请点击“打开窗口”或“前往页面”。')
    return
  }
  const hasForm = hasTaskConfigForm(form.taskType)
  let config = hasForm ? createDraft.value.config : (form.config.trim() || null)
  let total = hasForm ? createDraft.value.total : form.total
  if (hasForm && !createDraft.value.canSubmit) {
    ElMessage.warning(createDraft.value.validationError || '请先完善任务配置')
    return
  }
  if (config) {
    try {
      JSON.parse(config)
    } catch {
      ElMessage.warning('任务配置必须是合法 JSON')
      return
    }
  }
  form.total = Math.max(0, total)
  if (form.mode === 'scheduled' && !form.cronExpression.trim()) {
    ElMessage.warning('请填写 Cron 表达式')
    return
  }
  if (form.mode === 'scheduled' && !form.name.trim()) {
    ElMessage.warning('请填写计划任务名称')
    return
  }
  const taskDisplayName = form.name.trim()
  form.name = taskDisplayName
  createDialog.value.saving = true
  try {
    if (form.mode === 'scheduled') {
      await panelApi.createScheduledTask({
        name: taskDisplayName,
        taskType: form.taskType,
        total,
        configJson: config,
        cronExpression: form.cronExpression,
        status: 'enabled',
      })
      ElMessage.success('计划任务已创建')
    } else {
      await panelApi.createTask({
        taskType: form.taskType,
        name: taskDisplayName || null,
        total,
        config,
      })
      ElMessage.success('任务已提交')
    }
    createDialog.value.visible = false
    await load()
  } finally {
    createDialog.value.saving = false
  }
}

function resolveCreateTarget(definition?: TaskDefinition) {
  const taskType = definition?.taskType || ''
  const route = normalizeVueRoute(definition?.createRoute || '')

  if (route) return route
  return ''
}

function normalizeVueRoute(route: string) {
  let value = (route || '').trim()
  if (!value || /^https?:\/\//i.test(value)) return ''
  if (value.startsWith('/ui/')) value = value.slice(3)
  if (value === '/ui') value = '/'
  if (!value.startsWith('/')) value = `/${value}`
  return value
}

function goCreateTarget() {
  if (!currentCreateTarget.value) return
  createDialog.value.visible = false
  moduleWindow.value.visible = false
  if (isModuleEndpointRoute(currentCreateTarget.value)) {
    // “前往页面”是显式打开模块原生页，必须绕过宿主的默认 Vue 重定向。
    window.location.href = withModulePageMode(currentCreateTarget.value, false)
    return
  }
  router.push(currentCreateTarget.value)
}

function openCreateTargetWindow() {
  if (!currentCreateTarget.value) return
  const separator = currentCreateTarget.value.includes('?') ? '&' : '?'
  const src = isModuleEndpointRoute(currentCreateTarget.value)
    ? withModulePageMode(currentCreateTarget.value, true)
    : router.resolve(`${currentCreateTarget.value}${separator}embed=1`).href
  moduleWindow.value = {
    visible: true,
    title: `${taskName(createDialog.value.form.taskType)} - 任务配置`,
    src,
  }
}

function isModuleEndpointRoute(route: string) {
  return route.startsWith('/ext/')
}

function withModulePageMode(route: string, embedded: boolean) {
  const separator = route.includes('?') ? '&' : '?'
  return `${route}${separator}legacy=1${embedded ? '&embed=1' : ''}`
}

function defaultTotalForTask(taskType: string) {
  if (hasTaskConfigForm(taskType)) return 0
  return 0
}

function hasTaskConfigForm(taskType: string) {
  return isDirectMessagingTask(taskType)
    || taskType === 'user_chat_active'
    || taskType === 'channel_group_private_create'
    || taskType === 'channel_group_publicize'
    || taskType === 'fragment_username_monitor'
    || taskType === 'auto_change_login_email'
}

function isDirectMessagingTask(taskType: string) {
  return taskType === 'direct_message.live' || taskType === 'direct_message.batch'
}

function emptyDraft(): TaskConfigDraft {
  return { total: 0, config: null, canSubmit: false, validationError: null }
}

function onCreateDraftChanged(draft: TaskConfigDraft) {
  createDraft.value = draft
  createDialog.value.form.total = draft.total
}

function onEditDraftChanged(draft: TaskConfigDraft) {
  editDraft.value = draft
  editTaskDialog.value.form.total = draft.total
}

function onEditScheduledDraftChanged(draft: TaskConfigDraft) {
  editScheduledDraft.value = draft
  editScheduledDialog.value.form.total = draft.total
}

async function pauseTask(id: number) {
  await panelApi.pauseTask(id)
  await load()
}

async function resumeTask(id: number) {
  await panelApi.resumeTask(id)
  await load()
}

async function cancelTask(id: number) {
  await ElMessageBox.confirm(`确定要取消任务 #${id} 吗？`, '确认取消', { type: 'warning' })
  await panelApi.cancelTask(id)
  await load()
}

async function openEditTask(task: BatchTask) {
  const def = taskDefinition(task.taskType)
  if (!def?.canEdit) {
    ElMessage.warning('该任务当前不支持编辑')
    return
  }
  const status = displayStatus(task)
  if (status === 'pausing') {
    ElMessage.warning('任务仍在停止中，请等待状态变为已暂停后再编辑')
    return
  }
  if (status === 'pending' || status === 'running') {
    if (!def.autoPauseBeforeEdit) {
      ElMessage.warning('该任务尚未暂停，请先暂停后再编辑')
      return
    }
    await ElMessageBox.confirm(`任务 #${task.id} 尚未暂停，将先停止并等待旧执行实例退出，再打开编辑窗口。是否继续？`, '确认编辑', { type: 'warning' })
    await panelApi.pauseTask(task.id)
    await load()
  }
  const editRoute = resolveCreateTarget(def)
  if (editRoute && !hasTaskConfigForm(def.taskType)) {
    const separator = editRoute.includes('?') ? '&' : '?'
    const routeWithTaskId = `${editRoute}${separator}taskId=${encodeURIComponent(String(task.id))}&mode=edit`
    if (isModuleEndpointRoute(routeWithTaskId)) {
      window.location.href = withModulePageMode(routeWithTaskId, false)
    } else {
      await router.push(routeWithTaskId)
    }
    return
  }
  const fullTask = await loadTaskDetail(task.id)
  editDraft.value = emptyDraft()
  editTaskDialog.value = {
    visible: true,
    saving: false,
    id: fullTask.id,
    form: {
      taskType: fullTask.taskType,
      name: fullTask.name?.trim() || '',
      total: Math.max(0, fullTask.total),
      config: fullTask.config || '',
    },
  }
}

async function submitEditTask() {
  const dialog = editTaskDialog.value
  if (isDirectMessagingTask(dialog.form.taskType) && !editDraft.value.canSubmit) {
    ElMessage.warning(editDraft.value.validationError || '请先完善私信任务配置')
    return
  }
  const hasForm = hasTaskConfigForm(dialog.form.taskType)
  const config = hasForm ? editDraft.value.config : dialog.form.config.trim()
  const total = hasForm ? editDraft.value.total : dialog.form.total
  if (hasForm && !editDraft.value.canSubmit) {
    ElMessage.warning(editDraft.value.validationError || '请先完善任务配置')
    return
  }
  if (config) {
    try {
      JSON.parse(config)
    } catch {
      ElMessage.warning('配置 JSON 必须是合法 JSON')
      return
    }
  }
  await ElMessageBox.confirm(`将更新任务 #${dialog.id} 的配置，是否继续？`, '确认保存', { type: 'warning' })
  dialog.saving = true
  try {
    await panelApi.updateTask(dialog.id, {
      taskType: dialog.form.taskType,
      name: dialog.form.name.trim(),
      total: Math.max(0, total),
      config: config || null,
    })
    ElMessage.success(`任务 #${dialog.id} 配置已更新`)
    dialog.visible = false
    await load()
  } finally {
    dialog.saving = false
  }
}


async function deleteTask(task: BatchTask) {
  const isActive = isActiveStatus(displayStatus(task))
  const message = isActive
    ? `确定要删除任务 #${task.id} 吗？删除后将停止后台执行并移除记录，此操作不可恢复。`
    : `确定要删除任务 #${task.id} 的记录吗？此操作不可恢复。`
  await ElMessageBox.confirm(message, '确认删除', { type: 'warning' })
  await panelApi.deleteTask(task.id)
  await load()
}

async function rerunTask(task: BatchTask) {
  await ElMessageBox.confirm(`将基于任务 #${task.id} 的配置创建一个新任务，是否继续？`, '确认重新运行', { type: 'warning' })
  const created = await panelApi.rerunTask(task.id)
  ElMessage.success(`已创建重跑任务 #${created.id}`)
  await load()
}

async function copyTask(task: BatchTask) {
  const fullTask = await loadTaskDetail(task.id)
  openCopiedCreateDialog({
    sourceTaskId: fullTask.id,
    sourceDescription: `执行任务 #${fullTask.id}`,
    taskType: fullTask.taskType,
    name: fullTask.name?.trim() || '',
    mode: 'once',
    cronExpression: '0 9 * * *',
    total: Math.max(0, fullTask.total),
    config: fullTask.config ? stripRuntimeFields(fullTask.taskType, fullTask.config) : '',
  })
}

async function copyScheduledTask(task: ScheduledTask) {
  if (isDirectMessagingTask(task.taskType)) {
    ElMessage.warning('私信任务暂不支持 Cron 计划复制，请使用普通任务复制')
    return
  }
  const fullTask = await loadScheduledTaskDetail(task.id, task)
  openCopiedCreateDialog({
    sourceTaskId: fullTask.id,
    sourceDescription: `计划任务 #${fullTask.id}`,
    taskType: fullTask.taskType,
    name: scheduledName(fullTask),
    mode: 'scheduled',
    cronExpression: fullTask.cronExpression,
    total: Math.max(0, fullTask.total),
    config: fullTask.configJson ? stripRuntimeFields(fullTask.taskType, fullTask.configJson) : '',
  })
}

function openCopiedCreateDialog(draft: {
  sourceTaskId: number
  sourceDescription: string
  taskType: string
  name: string
  mode: string
  cronExpression: string
  total: number
  config: string
}) {
  const def = taskDefinition(draft.taskType)
  if (!def) {
    ElMessage.warning('该任务类型当前未安装或不可识别，无法复制')
    return
  }

  createDraft.value = emptyDraft()
  moduleWindow.value.visible = false
  createDialog.value.sourceTaskId = draft.sourceTaskId
  createDialog.value.sourceDescription = draft.sourceDescription
  createDialog.value.form = {
    category: def.category || '',
    taskType: draft.taskType,
    name: draft.name,
    mode: draft.mode,
    cronExpression: draft.cronExpression || '0 9 * * *',
    config: draft.config,
    total: Math.max(0, draft.total),
  }
  createDialog.value.visible = true
}

async function runScheduledNow(task: ScheduledTask) {
  if (isDirectMessagingTask(task.taskType)) {
    ElMessage.warning('私信任务暂不支持 Cron 计划执行')
    return
  }
  await ElMessageBox.confirm(
    `将立即按“${scheduledName(task)}”的当前配置创建一条执行任务，用于测试 Cron 配置效果。原计划仍会按下次运行时间继续调度，是否继续？`,
    '确认立即执行',
    { type: 'warning' },
  )
  const created = await panelApi.runScheduledTaskNow(task.id)
  ElMessage.success(`已创建执行任务 #${created.id}`)
  await load()
}

async function pauseScheduled(task: ScheduledTask) {
  await ElMessageBox.confirm(`确定要暂停“${scheduledName(task)}”吗？`, '确认暂停', { type: 'warning' })
  await panelApi.pauseScheduledTask(task.id)
  await load()
}

async function resumeScheduled(id: number) {
  await panelApi.resumeScheduledTask(id)
  await load()
}

async function deleteScheduled(task: ScheduledTask) {
  await ElMessageBox.confirm(`确定要删除“${scheduledName(task)}”吗？删除后不会影响已经触发的执行记录。`, '确认删除', { type: 'warning' })
  await panelApi.deleteScheduledTask(task.id)
  await load()
}

async function openEditScheduled(task: ScheduledTask) {
  if (isDirectMessagingTask(task.taskType)) {
    ElMessage.warning('私信任务暂不支持 Cron 计划编辑，请使用普通任务编辑')
    return
  }
  const fullTask = await loadScheduledTaskDetail(task.id, task)
  editScheduledDraft.value = emptyDraft()
  editScheduledDialog.value = {
    visible: true,
    saving: false,
    id: fullTask.id,
    form: {
      taskType: fullTask.taskType,
      name: fullTask.name || taskName(fullTask.taskType),
      total: Math.max(0, fullTask.total),
      configJson: fullTask.configJson || '',
      cronExpression: fullTask.cronExpression,
      status: fullTask.status === 'paused' ? 'paused' : 'enabled',
    },
  }
}

async function submitEditScheduled() {
  const dialog = editScheduledDialog.value
  if (isDirectMessagingTask(dialog.form.taskType)) {
    ElMessage.warning('私信任务暂不支持 Cron 计划编辑，请使用普通任务编辑')
    return
  }
  if (isDirectMessagingTask(dialog.form.taskType) && !editScheduledDraft.value.canSubmit) {
    ElMessage.warning(editScheduledDraft.value.validationError || '请先完善私信任务配置')
    return
  }
  const hasForm = hasTaskConfigForm(dialog.form.taskType)
  const config = hasForm ? editScheduledDraft.value.config : dialog.form.configJson.trim()
  const total = hasForm ? editScheduledDraft.value.total : dialog.form.total
  if (!dialog.form.name.trim()) {
    ElMessage.warning('请填写计划任务名称')
    return
  }
  if (!dialog.form.cronExpression.trim()) {
    ElMessage.warning('请填写 Cron 表达式')
    return
  }
  if (hasForm && !editScheduledDraft.value.canSubmit) {
    ElMessage.warning(editScheduledDraft.value.validationError || '请先完善任务配置')
    return
  }
  if (config) {
    try {
      JSON.parse(config)
    } catch {
      ElMessage.warning('配置 JSON 必须是合法 JSON')
      return
    }
  }
  dialog.form.name = dialog.form.name.trim()
  await ElMessageBox.confirm(`将更新“${dialog.form.name}”的配置，是否继续？`, '确认保存', { type: 'warning' })
  dialog.saving = true
  try {
    await panelApi.updateScheduledTask(dialog.id, {
      name: dialog.form.name,
      taskType: dialog.form.taskType,
      total: Math.max(0, total),
      configJson: config || null,
      cronExpression: dialog.form.cronExpression,
      status: dialog.form.status,
    })
    ElMessage.success(`计划任务 #${dialog.id} 已更新`)
    dialog.visible = false
    await load()
  } finally {
    dialog.saving = false
  }
}

async function showTaskDetails(task: BatchTask) {
  const fullTask = task.config == null ? await loadTaskDetail(task.id) : task
  task = fullTask
  const extraDetails = buildTaskExtraDetails(task)
  const lines = [
    `任务名称: ${batchTaskName(task)}`,
    `任务类型: ${taskName(task.taskType)}`,
    `归属模块: ${task.ownerModuleId || '-'}`,
    `执行通道: ${isPersistentTask(task) ? '常驻任务' : '批任务'}`,
    `状态: ${statusName(displayStatus(task))}`,
    `总数: ${task.total}`,
    `已完成: ${task.completed}`,
    `失败: ${task.failed}`,
    `创建时间: ${formatTime(task.createdAt)}`,
  ]
  if (task.startedAt) lines.push(`开始时间: ${formatTime(task.startedAt)}`)
  if (task.completedAt) lines.push(`完成时间: ${formatTime(task.completedAt)}`)
  if (task.runtimePhase) lines.push(`运行阶段: ${task.runtimePhase}`)
  if (task.runtimeMessage) lines.push(`运行消息: ${task.runtimeMessage}`)
  if (task.heartbeatAtUtc) lines.push(`最近心跳: ${formatTime(task.heartbeatAtUtc)}`)
  if (task.nextEligibleAtUtc) lines.push(`下次领取时间: ${formatTime(task.nextEligibleAtUtc)}`)
  if (task.requiresAttention) lines.push('提醒: 需要人工处理')
  if (extraDetails.length > 0) lines.push(...extraDetails)
  detailDialog.value = {
    visible: true,
    title: `任务详情 #${task.id}`,
    content: lines.join('\n'),
  }
}

async function loadTaskDetail(id: number) {
  try {
    return await panelApi.task(id)
  } catch {
    ElMessage.error(`无法加载任务 #${id} 的完整配置`)
    throw new Error(`Task ${id} load failed`)
  }
}

async function loadScheduledTaskDetail(id: number, fallback?: ScheduledTask) {
  if (fallback?.configJson != null) return fallback
  try {
    const task = await panelApi.scheduledTask(id)
    const index = scheduledTasks.value.findIndex((x) => x.id === id)
    if (index >= 0) scheduledTasks.value[index] = task
    return task
  } catch {
    ElMessage.error(`无法加载计划任务 #${id} 的完整配置`)
    throw new Error(`Scheduled task ${id} load failed`)
  }
}

function buildTaskExtraDetails(task: BatchTask) {
  const lines: string[] = []
  const config = (task.config || '').trim()
  if (!config) return lines

  const failureLines = extractBotAdminFailureLines(task)
  if (failureLines.length > 0) {
    lines.push('', '失败频道:', ...failureLines)
    return lines
  }

  if (isUserChatActiveTask(task.taskType)) {
    const activeDetails = buildUserChatActiveDetails(config)
    if (activeDetails.length > 0) return activeDetails
  }

  if (!isBotAdminTask(task.taskType)) {
    const configDetails = buildReadableConfigDetails(task.taskType, config)
    if (configDetails.length > 0) lines.push('', '配置摘要:', ...configDetails)
  }

  return lines
}

function isBotAdminTask(taskType: string) {
  return taskType === 'bot_channel_set_admins_by_account' || taskType === 'bot_set_admins'
}

function isUserChatActiveTask(taskType: string) {
  return taskType === 'user_chat_active'
}

function parseJsonObject(config: string): Record<string, any> | null {
  try {
    const value = JSON.parse(config)
    return value && typeof value === 'object' && !Array.isArray(value) ? value : null
  } catch {
    return null
  }
}

function stripRuntimeFields(taskType: string, config: string) {
  const obj = parseJsonObject(config)
  if (!obj) return config
  delete obj.items
  delete obj.recent_failures
  delete obj.failures
  if (taskType === 'fragment_username_monitor') {
    for (const key of ['StartedAtUtc', 'AssignedUsernames', 'LastCheckTime', 'Error', 'Canceled']) delete obj[key]
  }
  return JSON.stringify(obj, null, 2)
}

function buildReadableConfigDetails(taskType: string, config: string) {
  const obj = parseJsonObject(config)
  if (!obj) return config ? [`配置内容: ${config}`] : []

  if (taskType === 'channel_group_private_create') return buildPrivateCreateDetails(obj)
  if (taskType === 'channel_group_publicize') return buildPublicizeDetails(obj)
  if (taskType === 'account_auto_sync') return buildAccountSyncDetails(obj)
  if (taskType === 'auto_change_login_email') return buildAutoLoginEmailDetails(obj)
  if (taskType === 'fragment_username_monitor') return buildFragmentUsernameDetails(obj)


  if (taskType === 'user_join_subscribe') return buildUserJoinSubscribeDetails(obj)

  return buildGenericConfigDetails(obj)
}

function buildFragmentUsernameDetails(obj: Record<string, any>) {
  const usernames = readConfigArray(obj, 'Usernames', 'usernames')
  const targetGroupIds = readConfigArray(obj, 'TargetGroupIds', 'targetGroupIds')
  const assignedUsernames = readConfigArray(obj, 'AssignedUsernames', 'assignedUsernames')
  const durationHours = readConfigNumber(obj, 0, 'DurationHours', 'durationHours')
  const lines = [
    `监控用户名数: ${usernames.length}`,
    `目标频道分类: ${targetGroupIds.length > 0 ? targetGroupIds.map((x) => `#${x}`).join(', ') : '-'}`,
    `检查间隔: ${readConfigNumber(obj, 300, 'CheckIntervalSeconds', 'checkIntervalSeconds')} 秒`,
    `查询延迟: ${readConfigNumber(obj, 1500, 'QueryDelayMs', 'queryDelayMs')} ms`,
    `运行时长: ${durationHours <= 0 ? '持续运行' : `${durationHours} 小时`}`,
  ]
  if (assignedUsernames.length > 0) lines.push(`已抢注: ${assignedUsernames.join(', ')}`)
  const lastCheckTime = String(obj.LastCheckTime || obj.lastCheckTime || '').trim()
  if (lastCheckTime) lines.push(`上次检查: ${formatTime(lastCheckTime)}`)
  const error = String(obj.Error || obj.error || '').trim()
  if (error) lines.push(`错误: ${error}`)
  if (obj.Canceled === true || obj.canceled === true) lines.push('取消标记: 是')
  return lines
}

function readConfigArray(obj: Record<string, any>, ...keys: string[]) {
  for (const key of keys) {
    const value = obj[key]
    if (Array.isArray(value)) return value.map((x) => String(x ?? '').trim()).filter(Boolean)
  }
  return []
}

function readConfigNumber(obj: Record<string, any>, fallback: number, ...keys: string[]) {
  for (const key of keys) {
    const value = Number(obj[key])
    if (Number.isFinite(value)) return value
  }
  return fallback
}

function buildPrivateCreateDetails(obj: Record<string, any>) {
  const createType = String(obj.create_type || 'channel')
  const lines = [
    `账号分类: ${buildSelectedCategorySummary(obj)}`,
    `创建对象: ${objectTypeName(createType)}`,
    `${createType === 'group' ? '群组分类' : '频道分组'}: ${chatGroupName(obj, createType)}`,
    `每账号累计创建上限: ${formatNumberValue(obj.system_created_limit, 10)}`,
    `本轮每账号创建数: ${formatNumberValue(obj.per_account_batch_size, 1)}`,
    `间隔: ${formatSecondsValue(obj.min_delay_seconds)} ~ ${formatSecondsValue(obj.max_delay_seconds)} 秒`,
    `时间抖动: ${formatNumberValue(obj.jitter_percent, 0)}%`,
    `标题模板: ${formatTextValue(obj.title_template)}`,
    `头像来源: ${avatarSourceName(obj.avatar_source, obj)}`,
  ]

  const failures = buildChannelGroupAutomationFailureLines(obj)
  if (failures.length > 0) lines.push('', '最近失败:', ...failures)
  return lines
}

function buildChannelGroupAutomationFailureLines(obj: Record<string, any>) {
  if (!Array.isArray(obj.recent_failures)) return []

  return obj.recent_failures
    .map((item: any) => {
      const accountId = Number(item?.account_id || 0)
      const account = accountId > 0 ? `账号 #${accountId}` : '账号 -'
      const targetType = objectTypeName(item?.target_type)
      const target = String(item?.target || '').trim() || '-'
      const reason = String(item?.reason || '').trim() || '失败'
      const time = formatTime(item?.time_utc || '', '')
      return `${account} -> ${targetType}「${target}」：${reason}${time ? `（${time}）` : ''}`
    })
    .slice(-20)
}

function buildPublicizeDetails(obj: Record<string, any>) {
  const targetType = String(obj.target_type || 'channel')
  return [
    `账号分类: ${buildSelectedCategorySummary(obj)}`,
    `处理对象: ${objectTypeName(targetType)}`,
    `${targetType === 'group' ? '来源群组分类' : '来源频道分组'}: ${chatGroupName(obj, targetType)}`,
    `${targetType === 'group' ? '公开后群组分类' : '公开后频道分组'}: ${targetChatGroupName(obj, targetType)}`,
    `私密创建满天数: ${formatNumberValue(obj.min_system_created_days, 0)} 天`,
    `每账号公开保有上限: ${formatNumberValue(obj.max_public_count, 10)}`,
    `本轮每账号处理数: ${formatNumberValue(obj.per_account_batch_size, 1)}`,
    `间隔: ${formatSecondsValue(obj.min_delay_seconds)} ~ ${formatSecondsValue(obj.max_delay_seconds)} 秒`,
    `时间抖动: ${formatNumberValue(obj.jitter_percent, 0)}%`,
    `标题模板: ${formatTextValue(obj.title_template)}`,
    `描述模板: ${formatTextValue(obj.description_template)}`,
    `公开用户名模板: ${formatTextValue(obj.username_template)}`,
    `头像来源: ${avatarSourceName(obj.avatar_source, obj)}`,
  ]
}


function buildAccountSyncDetails(obj: Record<string, any>) {
  const lines = [
    `触发方式: ${syncTriggerName(obj.trigger)}`,
    `同步范围: ${syncScopeName(obj.scope)}`,
  ]
  if (Array.isArray(obj.includes)) lines.push(`包含内容: ${obj.includes.map(syncFlagName).join(', ') || '-'}`)
  if (Array.isArray(obj.excludes)) lines.push(`排除内容: ${obj.excludes.map(syncFlagName).join(', ') || '-'}`)
  if (obj.progress && typeof obj.progress === 'object') {
    const progress = obj.progress as Record<string, any>
    lines.push(`进度记录: 账号 ${formatNumberValue(progress.processedAccounts, 0)} / ${formatNumberValue(progress.totalAccounts, 0)}，失败 ${formatNumberValue(progress.failedAccounts, 0)}`)
  }
  if (obj.result && typeof obj.result === 'object') {
    const result = obj.result as Record<string, any>
    lines.push(`同步结果: 频道 ${formatNumberValue(result.totalChannelsSynced, 0)}，群组 ${formatNumberValue(result.totalGroupsSynced, 0)}`)
  }
  if (Array.isArray(obj.failures) && obj.failures.length > 0) {
    lines.push(`失败记录: ${obj.failures.length} 条`, '失败账号:')
    lines.push(...obj.failures.slice(-80).map((item: any) => {
      const accountId = Number(item?.accountId ?? item?.account_id ?? item?.AccountId ?? 0)
      const phone = String(item?.phone ?? item?.Phone ?? '').trim() || '-'
      const error = String(item?.error ?? item?.Error ?? '').trim() || '失败'
      return `账号 #${accountId || '-'} ${phone}：${error}`
    }))
    if (obj.failures.length > 80) lines.push(`... 仅展示最后 80 项（共 ${obj.failures.length} 项）`)
  }
  if (obj.error) lines.push(`错误: ${formatConfigValue(obj.error)}`)
  return lines
}

function buildUserJoinSubscribeDetails(obj: Record<string, any>) {
  const lines = [
    `操作: ${obj.operation === 'leave' ? '退群/退订/停用 Bot' : '加群/订阅/启用 Bot'}`,
    `账号数: ${Array.isArray(obj.accountIds) ? obj.accountIds.length : 0}`,
    `目标数: ${Array.isArray(obj.links) ? obj.links.length : 0}`,
    `间隔: ${formatDelaySeconds(Number(obj.delayMs || 0))} 秒`,
  ]

  if (Array.isArray(obj.failures) && obj.failures.length > 0) {
    lines.push('', `失败记录: ${obj.failures.length} 项`)
    lines.push(...obj.failures.slice(-80).map((item: any, index: number) => {
      const accountId = Number(item?.accountId || item?.account_id || 0)
      const target = String(item?.target || '').trim() || '-'
      const reason = String(item?.reason || '').trim() || '失败'
      return `${index + 1}. 账号 #${accountId || '-'} -> ${target}：${reason}`
    }))
    if (obj.failures.length > 80) lines.push(`... 仅展示最后 80 项（共 ${obj.failures.length} 项）`)
  }

  if (obj.error) lines.push(`错误: ${formatConfigValue(obj.error)}`)
  return lines
}

function buildAutoLoginEmailDetails(obj: Record<string, any>) {
  const windowHours = Number(obj.trigger_window_hours ?? 24)
  const domains = Array.isArray(obj.domains) ? obj.domains.map((x: any) => String(x || '').trim()).filter(Boolean) : []
  const domainText = domains.length > 0 ? domains.join(', ') : formatConfigValue(obj.domain)
  const lines = [
    `账号分类: ${buildSelectedCategorySummary(obj)}`,
    `邮箱域名池: ${domainText}`,
    `通知窗口: 距今 ${formatNumberValue(obj.trigger_days_ago, 6)} 天，±${Number.isFinite(windowHours) ? windowHours / 2 : 12} 小时`,
    `最多扫描通知: ${formatNumberValue(obj.max_system_messages, 300)}`,
    `强制执行: ${obj.force ? '是' : '否'}`,
    `自动确认: ${obj.auto_confirm === false ? '否' : '是'}`,
  ]
  if (Array.isArray(obj.items) && obj.items.length > 0) {
    lines.push('', '最近结果:')
    lines.push(...obj.items.slice(-20).map((item: any) => {
      const accountId = Number(item?.account_id || 0)
      const phone = String(item?.phone || '').trim() || '-'
      const result = String(item?.result || '').trim() || '-'
      const email = String(item?.email || '').trim()
      const previousDomain = String(item?.previous_login_email_domain || '').trim()
      const message = String(item?.message || '').trim() || '-'
      const time = formatTime(item?.time_utc || '', '')
      const emailText = email ? ` -> ${email}` : ''
      const previousText = previousDomain ? `（原域名 ${previousDomain}）` : ''
      return `账号 #${accountId || '-'} ${phone}${emailText}：${result}${previousText}，${message}${time ? `（${time}）` : ''}`
    }))
  }
  return lines
}

function buildGenericConfigDetails(obj: Record<string, any>) {
  const skipKeys = new Set(['recent_failures'])
  return Object.entries(obj)
    .filter(([key, value]) => !skipKeys.has(key) && !isEmptyConfigValue(value))
    .map(([key, value]) => `${configKeyName(key)}: ${formatConfigValue(value)}`)
}

function isEmptyConfigValue(value: unknown) {
  if (value == null || value === '') return true
  if (Array.isArray(value)) return value.length === 0
  if (typeof value === 'object') return Object.keys(value as Record<string, unknown>).length === 0
  return false
}

function formatConfigValue(value: unknown): string {
  if (value == null || value === '') return '-'
  if (typeof value === 'boolean') return value ? '是' : '否'
  if (typeof value === 'number') return Number.isFinite(value) ? String(value) : '-'
  if (typeof value === 'string') return value.trim() || '-'
  if (Array.isArray(value)) {
    if (value.length === 0) return '-'
    const simpleItems = value.filter((item) => item == null || ['string', 'number', 'boolean'].includes(typeof item))
    if (simpleItems.length === value.length) {
      const values = simpleItems.slice(0, 8).map(formatConfigValue)
      return value.length > values.length ? `${values.join(', ')} 等 ${value.length} 项` : values.join(', ')
    }
    return `${value.length} 项`
  }
  if (typeof value === 'object') {
    const entries = Object.entries(value as Record<string, unknown>)
      .filter(([, item]) => !isEmptyConfigValue(item))
      .slice(0, 8)
      .map(([key, item]) => `${configKeyName(key)} ${formatConfigValue(item)}`)
    return entries.length > 0 ? entries.join('，') : '-'
  }
  return String(value)
}

function configKeyName(key: string) {
  const labels: Record<string, string> = {
    category_id: '账号分类ID',
    category_ids: '账号分类ID',
    category_name: '账号分类',
    category_names: '账号分类',
    create_type: '创建对象',
    target_type: '处理对象',
    channel_group_id: '频道分组ID',
    channel_group_name: '频道分组',
    group_category_id: '群组分类ID',
    group_category_name: '群组分类',
    system_created_limit: '每账号累计创建上限',
    per_account_batch_size: '本轮每账号数量',
    min_delay_seconds: '最小间隔秒数',
    max_delay_seconds: '最大间隔秒数',
    delay_min_ms: '最小间隔毫秒',
    delay_max_ms: '最大间隔毫秒',
    jitter_percent: '时间抖动',
    title_template: '标题模板',
    description_template: '描述模板',
    username_template: '公开用户名模板',
    avatar_source: '头像来源',
    fixed_avatar_asset_path: '固定头像',
    avatar_dictionary_token: '头像图片字典',
    image_dictionary_token: '图片字典',
    asset_scope_id: '资源作用域',
    targets: '目标',
    message_action_mode: '发送动作',
    reply_to_message_url: '回复消息链接',
    forward_source_urls: '转发来源消息',
    forward_mode: '转发方式',
    skip_if_last_message_from_self: '去重发送',
    dictionary: '文字词典',
    message_rules: '消息规则',
    account_mode: '账号模式',
    target_mode: '目标模式',
    message_mode: '消息规则模式',
    max_messages: '最多发送',
    enable_ai_verification: 'AI 验证',
    ai_model: 'AI 模型',
    verification_timeout_seconds: '验证超时秒数',
    verification_timeout_as_failure: '超时计失败',
    verification_match_mode: '验证匹配方式',
    verification_keywords: '验证关键词',
    verification_regexes: '验证正则',
    verification_bot_usernames: '验证机器人',
    trigger: '触发方式',
    scope: '同步范围',
    includes: '包含内容',
    excludes: '排除内容',
    progress: '进度',
    result: '结果',
    failures: '失败记录',
    error: '错误',
  }
  return labels[key] || key.replace(/_/g, ' ')
}

function objectTypeName(value: unknown) {
  const text = String(value || '').trim()
  if (text === 'group') return '群组'
  if (text === 'channel') return '频道'
  return text || '-'
}

function chatGroupName(obj: Record<string, any>, type: string) {
  const nameKey = type === 'group' ? 'group_category_name' : 'channel_group_name'
  const idKey = type === 'group' ? 'group_category_id' : 'channel_group_id'
  const name = String(obj[nameKey] || '').trim()
  const id = Number(obj[idKey] || 0)
  if (name && id > 0) return `${name} (#${id})`
  if (name) return name
  if (id > 0) return `#${id}`
  return type === 'group' ? '未分类' : '未分组'
}

function targetChatGroupName(obj: Record<string, any>, type: string) {
  const isGroup = type === 'group'
  const idKey = isGroup ? 'target_group_category_id' : 'target_channel_group_id'
  if (obj[idKey] === null || obj[idKey] === undefined) return isGroup ? '保持原分类' : '保持原分组'
  const nameKey = isGroup ? 'target_group_category_name' : 'target_channel_group_name'
  const name = String(obj[nameKey] || '').trim()
  const id = Number(obj[idKey] || 0)
  if (name && id > 0) return `${name} (#${id})`
  if (name) return name
  if (id > 0) return `#${id}`
  return isGroup ? '未分类' : '未分组'
}

function avatarSourceName(value: unknown, obj: Record<string, any>) {
  const source = String(value || 'none').trim()
  if (source === 'fixed') return `固定上传${obj.fixed_avatar_asset_path ? `（${obj.fixed_avatar_asset_path}）` : ''}`
  if (source === 'dictionary') return `图片字典${obj.avatar_dictionary_token ? `（${obj.avatar_dictionary_token}）` : ''}`
  return '不设置'
}

function formatNumberValue(value: unknown, fallback: number) {
  const number = Number(value)
  return Number.isFinite(number) ? String(number) : String(fallback)
}

function formatTextValue(value: unknown) {
  return String(value || '').trim() || '-'
}

function formatSecondsValue(value: unknown) {
  const number = Number(value)
  if (!Number.isFinite(number) || number <= 0) return '0'
  return number.toLocaleString('zh-CN', { maximumFractionDigits: 3 })
}


function syncTriggerName(value: unknown) {
  const trigger = String(value || '').trim()
  if (trigger === 'auto') return '自动'
  if (trigger === 'manual') return '手动'
  return trigger || '-'
}

function syncScopeName(value: unknown) {
  const scope = String(value || '').trim()
  if (scope === 'all_active_accounts') return '全部活跃账号'
  return scope || '-'
}

function syncFlagName(value: unknown) {
  const flag = String(value || '').trim()
  const labels: Record<string, string> = {
    visible_channels_sync: '同步可见频道',
    visible_groups_sync: '同步可见群组',
    lightweight_telegram_status_refresh_on_sync_error: '同步异常时轻量刷新账号状态',
    successful_sync_clears_transient_telegram_status: '同步成功清除临时异常状态',
    deep_telegram_status_probe: '深度探测 Telegram 状态',
    verification_code_collection: '收集验证码',
  }
  return labels[flag] || flag
}

function extractBotAdminFailureLines(task: BatchTask) {
  if (!isBotAdminTask(task.taskType) || !task.config || task.failed <= 0) return []

  const obj = parseJsonObject(task.config)
  if (!obj) return []

  if (Array.isArray(obj.FailureLines)) {
    const directLines = obj.FailureLines.map((x) => String(x || '').trim()).filter(Boolean)
    if (directLines.length > 0) return directLines
  }

  if (!Array.isArray(obj.Failures)) return []

  return obj.Failures
    .map((item: any) => {
      const title = String(item?.ChannelTitle || '').trim()
      const channelId = Number(item?.ChannelTelegramId || 0)
      const username = String(item?.Username || '').trim()
      const userId = item?.UserId ? String(item.UserId).trim() : ''
      const target = username ? `@${username.replace(/^@/, '')}` : userId
      const reason = String(item?.Reason || '').trim() || '失败'
      const channel = title || (channelId > 0 ? String(channelId) : '-')
      return `${channel}${target ? ` -> ${target}` : ''}：${reason}`
    })
    .filter(Boolean)
}

function buildUserChatActiveDetails(config: string) {
  const obj = parseJsonObject(config)
  if (!obj) return []

  const categoryText = buildSelectedCategorySummary(obj)
  const targetCount = Array.isArray(obj.targets) ? obj.targets.length : 0
  const dictionaryCount = Array.isArray(obj.dictionary) ? obj.dictionary.length : 0
  const messageRuleCount = Array.isArray(obj.message_rules) && obj.message_rules.length > 0 ? obj.message_rules.length : dictionaryCount
  const actionMode = String(obj.message_action_mode || 'send_generated_text')
  const isForwardMode = actionMode === 'forward_url'
  const forwardSourceCount = Array.isArray(obj.forward_source_urls) ? obj.forward_source_urls.length : 0
  const skipIfLastMessageFromSelf = obj.skip_if_last_message_from_self === true
  const delayMin = Number(obj.delay_min_ms || 0)
  const delayMax = Number(obj.delay_max_ms || 0)
  const maxMessages = Number(obj.max_messages || 0)

  const lines = [
    '',
    '配置摘要:',
    `分类: ${categoryText}`,
    `目标数: ${targetCount}`,
    `发送动作: ${isForwardMode ? '转发消息链接' : '发送消息规则'}`,
    isForwardMode ? `转发来源数: ${forwardSourceCount}` : `消息规则数: ${messageRuleCount}`,
    isForwardMode ? `转发方式: ${obj.forward_mode === 'hide_attribution' ? '不带引用转发' : '带引用转发'}` : `回复消息: ${String(obj.reply_to_message_url || '').trim() || '未设置'}`,
    `去重发送: ${skipIfLastMessageFromSelf ? '启用（上一条仍是当前账号则跳过）' : '关闭'}`,
    `账号模式: ${normalizeTaskModeDisplay(obj.account_mode)}`,
    `目标模式: ${normalizeTaskModeDisplay(obj.target_mode)}`,
    ...(isForwardMode ? [] : [`内容模式: ${normalizeTaskModeDisplay(obj.message_mode)}`]),
    `间隔: ${formatDelaySeconds(delayMin)} ~ ${formatDelaySeconds(delayMax)} 秒`,
    `最多发送: ${maxMessages <= 0 ? '持续运行（直到取消）' : maxMessages}`,
  ]

  const failures = Array.isArray(obj.recent_failures)
    ? obj.recent_failures
        .map((item: any, index: number) => {
          const accountId = Number(item?.account_id || item?.accountId || 0)
          const account = String(item?.account || '').trim() || (accountId > 0 ? `账号 #${accountId}` : '账号 -')
          const target = String(item?.target || '').trim() || '-'
          const reason = String(item?.reason || '').trim() || '失败'
          const time = formatTime(item?.time_utc || item?.timeUtc || '', '')
          return `${index + 1}. ${account} -> ${target}：${reason}${time ? `（${time}）` : ''}`
        })
        .slice(-80)
    : []

  if (failures.length > 0) {
    lines.push('', `失败记录: ${obj.recent_failures.length} 项`, ...failures)
    if (obj.recent_failures.length > 80) lines.push(`... 仅展示最后 80 项（共 ${obj.recent_failures.length} 项）`)
  }
  return lines
}

function buildSelectedCategorySummary(obj: Record<string, any>) {
  const ids = Array.isArray(obj.category_ids) ? obj.category_ids.filter((x: any) => Number(x) > 0).map((x: any) => Number(x)) : []
  if (ids.length === 0 && Number(obj.category_id) > 0) ids.push(Number(obj.category_id))

  const names = Array.isArray(obj.category_names)
    ? obj.category_names.map((x: any) => String(x || '').trim()).filter(Boolean)
    : []
  if (names.length === 0 && String(obj.category_name || '').trim()) names.push(String(obj.category_name).trim())

  const count = Math.max(ids.length, names.length)
  if (count === 0) return '-'

  const parts: string[] = []
  for (let i = 0; i < count; i += 1) {
    const id = ids[i] || 0
    const name = names[i] || ''
    if (id > 0 && name) parts.push(`${name} (#${id})`)
    else if (id > 0) parts.push(`#${id}`)
    else if (name) parts.push(name)
  }
  return parts.length === 0 ? '-' : parts.join(', ')
}

function normalizeTaskModeDisplay(value: unknown) {
  const mode = String(value || '').trim()
  if (mode === 'random') return '随机'
  if (mode === 'queue') return '队列循环'
  return mode || '-'
}

function formatDelaySeconds(milliseconds: number) {
  if (!Number.isFinite(milliseconds) || milliseconds <= 0) return '0'
  return (milliseconds / 1000).toLocaleString('zh-CN', { maximumFractionDigits: 3 })
}

async function showScheduledDetails(task: ScheduledTask) {
  task = await loadScheduledTaskDetail(task.id, task)
  const lines = [
    `任务名称: ${scheduledName(task)}`,
    `任务类型: ${taskName(task.taskType)}`,
    `状态: ${task.status === 'paused' ? '已暂停' : '启用中'}`,
    `Cron: ${task.cronExpression}`,
    `总数: ${task.total}`,
    `创建时间: ${formatTime(task.createdAt)}`,
    `更新时间: ${formatTime(task.updatedAt)}`,
  ]
  if (task.nextRunAtUtc) lines.push(`下次运行: ${formatTime(task.nextRunAtUtc)}`)
  if (task.lastRunAtUtc) lines.push(`上次运行: ${formatTime(task.lastRunAtUtc)}`)
  if (task.lastBatchTaskId) lines.push(`最近批次任务: #${task.lastBatchTaskId}`)
  if (task.configJson) {
    const configDetails = buildReadableConfigDetails(task.taskType, task.configJson)
    if (configDetails.length > 0) lines.push('', '配置摘要:', ...configDetails)
  }
  detailDialog.value = {
    visible: true,
    title: `计划任务详情：${scheduledName(task)}`,
    content: lines.join('\n'),
  }
}

function statusName(status: string) {
  if (status === 'initializing') return '正在初始化'
  if (status === 'updating') return '正在提交配置'
  if (status === 'pending') return '待执行'
  if (status === 'running') return '执行中'
  if (status === 'pausing') return '停止中'
  if (status === 'paused') return '已暂停'
  if (status === 'completed') return '已完成'
  if (status === 'failed') return '失败'
  if (status === 'canceled') return '已取消'
  return status
}

async function openCleanupDialog() {
  const hasActive = tasks.value.some((x) => isActiveStatus(displayStatus(x)))
  const mode = await ElMessageBox.confirm(
    hasActive
      ? '当前包含执行中/待执行/已暂停任务。选择“清理全部”会先将活跃任务标记失败并删除全部记录。'
      : '请选择清理全部任务记录，或取消后仅在历史任务列表逐条删除。',
    '确认清理',
    {
      type: 'warning',
      confirmButtonText: '清理全部',
      cancelButtonText: '只清理历史',
      distinguishCancelAndClose: true,
    },
  ).then(() => 'all' as const).catch((action) => {
    if (action === 'cancel') return 'history' as const
    throw action
  })

  await panelApi.cleanupTasks(mode)
  await load()
}

onMounted(() => {
  load()
  timer = window.setInterval(() => {
    if (document.visibilityState === 'visible' && needsAutoRefresh.value) {
      void load({ silent: true }).catch(() => undefined)
    }
  }, 8000)
})
onUnmounted(() => {
  if (timer) window.clearInterval(timer)
})
</script>

<style scoped>
.task-toolbar {
  min-height: 36px;
}

.toolbar-title {
  font-size: 16px;
  font-weight: 600;
}

.toolbar-spacer {
  flex: 1;
}

.full {
  width: 100%;
}

.detail-pre {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: Consolas, "Microsoft YaHei", monospace;
  line-height: 1.55;
}

:global(.task-dialog) {
  max-height: calc(100vh - 24px);
  display: flex;
  flex-direction: column;
}

:global(.task-dialog .el-dialog__body) {
  flex: 1 1 auto;
  overflow-y: auto;
}

:global(.task-dialog .el-dialog__footer) {
  flex: 0 0 auto;
}

@media (max-width: 640px) {
  :global(.task-dialog) {
    margin-top: 12px;
    margin-bottom: 12px;
  }

  :global(.task-dialog .el-dialog__body) {
    padding: 12px 16px;
  }

  :global(.task-dialog .el-dialog__footer) {
    padding: 12px 16px 16px;
  }
}
</style>
