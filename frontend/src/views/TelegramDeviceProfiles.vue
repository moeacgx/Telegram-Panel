<template>
  <div>
    <el-alert
      class="mb-4"
      type="info"
      :closable="false"
      show-icon
      :title="`写入位置：${settings?.localConfigPath || '-'}`"
    />

    <el-card shadow="never" class="page-card">
      <template #header>设备指纹目录</template>
      <el-form label-position="top">
        <el-form-item label="默认设备指纹">
          <el-select v-model="defaultDeviceProfileKey" class="full" filterable>
            <el-option label="随机设备指纹" value="random" />
            <el-option
              v-for="profile in deviceProfiles"
              :key="profile.key"
              :label="`${profile.name} · ${profile.family}`"
              :value="profile.key"
            />
          </el-select>
          <div v-if="randomDeviceProfileSelected" class="muted mt-2">
            按账号/会话稳定随机选择适合当前 API 的内置画像，避免所有新账号共用同一设备参数。
          </div>
          <div v-else-if="selectedDeviceProfile" class="muted mt-2">
            {{ selectedDeviceProfile.deviceModel }} · {{ selectedDeviceProfile.systemVersion }} · App {{ selectedDeviceProfile.appVersion }}
          </div>
          <div class="muted mt-2">这里管理 Telegram 客户端的设备画像目录和新账号/导入/连接使用的默认项；单个账号仍可在账号详情里单独覆盖。</div>
        </el-form-item>
      </el-form>

      <el-table v-loading="loading" :data="deviceProfiles" stripe class="mt-3">
        <el-table-column label="Key" min-width="180" prop="key" />
        <el-table-column label="名称" min-width="180" prop="name" />
        <el-table-column label="Family" width="120" prop="family" />
        <el-table-column label="设备参数" min-width="260">
          <template #default="{ row }">
            <div class="cell-main">{{ row.deviceModel }}</div>
            <div class="cell-sub">{{ row.systemVersion }} · App {{ row.appVersion }}</div>
          </template>
        </el-table-column>
        <el-table-column label="语言" width="160">
          <template #default="{ row }">
            <div>{{ row.systemLangCode }}</div>
            <div class="cell-sub">{{ row.langCode }}</div>
          </template>
        </el-table-column>
        <el-table-column label="来源" width="100">
          <template #default="{ row }">
            <el-tag :type="row.builtIn ? 'success' : 'info'" size="small">{{ row.builtIn ? '内置' : '自定义' }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column label="备注" min-width="180">
          <template #default="{ row }">{{ row.notes || '-' }}</template>
        </el-table-column>
      </el-table>

      <div class="button-row mt-3">
        <el-button type="primary" :loading="saving" @click="saveDefaultDeviceProfile">保存默认画像</el-button>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { ElMessage } from 'element-plus'
import { panelApi } from '@/api/panel'
import type { SettingsPayload, TelegramApiSettings, TelegramDeviceProfile } from '@/api/types'

const settings = ref<SettingsPayload | null>(null)
const loading = ref(false)
const deviceProfiles = ref<TelegramDeviceProfile[]>([])
const defaultDeviceProfileKey = ref('')
const saving = ref(false)

const randomDeviceProfileSelected = computed(() => defaultDeviceProfileKey.value === 'random')
const selectedDeviceProfile = computed(() => deviceProfiles.value.find((profile) => profile.key === defaultDeviceProfileKey.value))


function normalizeTelegramSettings(source: TelegramApiSettings) {
  deviceProfiles.value = source.deviceProfiles || []
  defaultDeviceProfileKey.value = source.defaultDeviceProfileKey || deviceProfiles.value[0]?.key || ''
}

async function load() {
  loading.value = true
  try {
    const data = await panelApi.settings()
    settings.value = data
    normalizeTelegramSettings(data.telegram)
  } finally {
    loading.value = false
  }
}

async function saveDefaultDeviceProfile() {
  saving.value = true
  try {
    const requestedDefaultKey = defaultDeviceProfileKey.value
    const current = await panelApi.settings()
    const result = await panelApi.saveTelegramApiSettings({
      apiId: current.telegram.apiId,
      apiHash: current.telegram.apiHash,
      profiles: current.telegram.profiles,
      officialApiEnabled: current.telegram.officialApiEnabled !== false,
      deviceProfiles: current.telegram.deviceProfiles || [],
      defaultDeviceProfileKey: requestedDefaultKey,
    })
    if (result.message) ElMessage.success(result.message)
    await load()
    defaultDeviceProfileKey.value = requestedDefaultKey
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.button-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.full {
  width: 100%;
}

.mb-4 {
  margin-bottom: 16px;
}

.mt-2 {
  margin-top: 8px;
}

.mt-3 {
  margin-top: 12px;
}

</style>
