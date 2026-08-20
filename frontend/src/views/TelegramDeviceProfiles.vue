<template>
  <div>
    <el-alert
      class="mb-4"
      type="info"
      :closable="false"
      show-icon
      :title="`写入位置：${settings?.localConfigPath || '-'}`"
    />

    <div class="settings-columns">
      <div class="settings-column">
        <el-card shadow="never" class="page-card">
          <template #header>设备指纹目录</template>
          <el-form label-position="top">
            <el-form-item label="默认设备指纹">
              <el-select v-model="defaultDeviceProfileKey" class="full" filterable>
                <el-option
                  v-for="profile in deviceProfiles"
                  :key="profile.key"
                  :label="`${profile.name} · ${profile.family}`"
                  :value="profile.key"
                />
              </el-select>
              <div v-if="selectedDeviceProfile" class="muted mt-2">
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

      <div class="settings-column">
        <el-card shadow="never" class="page-card">
          <template #header>Telegram API 状态</template>
          <el-alert type="info" :closable="false" show-icon class="mb-3">
            <template #title>Telegram API 池在系统设置中管理。</template>
            <div>这里仅显示当前生效 ApiId、API 来源、启用的自定义 API 数量和默认设备指纹。</div>
          </el-alert>
          <el-descriptions :column="1" border size="small">
            <el-descriptions-item label="当前生效 ApiId">{{ effectiveApiId || '（不可用）' }}</el-descriptions-item>
            <el-descriptions-item label="当前 API 来源">{{ effectiveApiSourceLabel }}</el-descriptions-item>
            <el-descriptions-item label="启用中的自定义 API">{{ enabledApiProfiles.length }}</el-descriptions-item>
            <el-descriptions-item label="当前默认设备指纹">
              {{ selectedDeviceProfile ? `${selectedDeviceProfile.name} · ${selectedDeviceProfile.family}` : '（未配置）' }}
            </el-descriptions-item>
          </el-descriptions>
          <div class="button-row mt-3">
            <el-button type="primary" plain @click="router.push('/settings')">去系统设置</el-button>
          </div>
        </el-card>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { panelApi } from '@/api/panel'
import type { SettingsPayload, TelegramApiSettings, TelegramDeviceProfile } from '@/api/types'

const router = useRouter()
const settings = ref<SettingsPayload | null>(null)
const loading = ref(false)
const deviceProfiles = ref<TelegramDeviceProfile[]>([])
const defaultDeviceProfileKey = ref('')
const saving = ref(false)

const selectedDeviceProfile = computed(() => deviceProfiles.value.find((profile) => profile.key === defaultDeviceProfileKey.value))
const enabledApiProfiles = computed(() => (settings.value?.telegram.profiles || []).filter((profile) => profile.enabled !== false))
const effectiveApiId = computed(() => settings.value?.telegram.effectiveApiId || settings.value?.system.effectiveApiId || '')
const effectiveApiSourceLabel = computed(() => {
  const source = settings.value?.telegram.effectiveApiSource
  if (source === 'built_in_official') return '内置官方 Android API'
  if (source === 'api_profile') return settings.value?.telegram.effectiveApiName || 'API 配置池'
  if (source === 'custom_default') return '旧版单 API'
  if (source === 'invalid') return '配置不可用'
  return settings.value ? '未配置' : '加载中'
})

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
    const current = await panelApi.settings()
    const result = await panelApi.saveTelegramApiSettings({
      apiId: current.telegram.apiId,
      apiHash: current.telegram.apiHash,
      profiles: current.telegram.profiles,
      officialApiEnabled: current.telegram.officialApiEnabled !== false,
      deviceProfiles: current.telegram.deviceProfiles || [],
      defaultDeviceProfileKey: defaultDeviceProfileKey.value,
    })
    if (result.message) ElMessage.success(result.message)
    await load()
  } finally {
    saving.value = false
  }
}

onMounted(load)
</script>

<style scoped>
.settings-columns {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 16px;
}

.settings-column {
  display: grid;
  gap: 16px;
  align-content: start;
}

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

@media (max-width: 960px) {
  .settings-columns {
    grid-template-columns: 1fr;
  }
}
</style>
