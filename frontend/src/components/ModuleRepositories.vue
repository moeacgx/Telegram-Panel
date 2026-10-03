<template>
  <el-card shadow="never" class="page-card mt-4">
    <div class="repository-toolbar">
      <strong>在线模块仓库</strong>
      <el-button type="primary" :disabled="busy" @click="edit()">添加仓库</el-button>
    </div>
    <p class="muted">可连接官方、个人公开或私有仓库。模块与面板在同一进程运行，请仅安装可信作者的模块。</p>
    <nav class="repository-toolbar repository-guide" aria-label="模块仓库开发与维护">
      <el-link type="primary" href="https://github.com/moeacgx/Telegram-Panel-Modules" target="_blank" rel="noopener noreferrer">官方模块仓库</el-link>
      <el-link type="primary" href="https://github.com/moeacgx/Telegram-Panel-Modules/fork" target="_blank" rel="noopener noreferrer">Fork 创建个人仓库</el-link>
      <el-link type="primary" href="https://github.com/moeacgx/Telegram-Panel-Modules#readme" target="_blank" rel="noopener noreferrer">仓库维护说明</el-link>
    </nav>
    <p class="muted">想修改演示模块或发布自己的模块？先 Fork 官方仓库，按维护说明修改源码、构建并发布模块包、更新目录，再点击“添加仓库”填写你的 owner/repo。私有仓库还需配置只读访问令牌。</p>
    <div class="repository-toolbar">
      <el-select v-model="selectedId" placeholder="选择仓库" :disabled="busy" @change="clearCatalog" style="min-width: 260px">
        <el-option v-for="repo in repositories" :key="repo.id" :label="repo.name" :value="repo.id" />
      </el-select>
      <el-button :disabled="!selected || busy" @click="fetchCatalog">连接 / 刷新目录</el-button>
      <el-button :disabled="!selected || busy" @click="edit(selected)">编辑仓库</el-button>
      <el-button type="danger" plain :disabled="!selected || busy" @click="remove">移除仓库</el-button>
    </div>
    <p v-if="selected" class="muted">{{ selected.location }} · {{ selected.hasToken ? '已配置令牌' : '未配置令牌' }}</p>
    <el-alert v-if="error" :title="error" type="error" :closable="false" class="mb-3" />
    <el-table v-loading="busy" :data="catalog" stripe empty-text="连接仓库后显示可安装的模块；空仓库暂未发布模块">
      <el-table-column label="模块" min-width="230">
        <template #default="{ row }"><strong>{{ row.name }}</strong><div class="muted">{{ row.id }}</div><div>{{ row.description }}</div></template>
      </el-table-column>
      <el-table-column prop="version" label="版本" width="130" />
      <el-table-column label="操作" width="130">
        <template #default="{ row }">
          <el-button type="primary" link :disabled="busy || isInstalled(row)" @click="install(row)">{{ isInstalled(row) ? '已安装' : '安装此版本' }}</el-button>
        </template>
      </el-table-column>
    </el-table>
    <el-dialog v-model="dialog" :title="editingId ? '编辑模块仓库' : '添加模块仓库'" width="min(620px, 95vw)" :close-on-click-modal="false" @closed="form.token = ''">
      <el-form label-position="top" @submit.prevent="save">
        <el-form-item label="名称"><el-input v-model="form.name" maxlength="100" /></el-form-item>
        <el-form-item label="仓库类型">
          <el-radio-group v-model="form.kind"><el-radio value="github">GitHub 仓库</el-radio><el-radio value="https">HTTPS 目录</el-radio></el-radio-group>
        </el-form-item>
        <el-form-item :label="form.kind === 'github' ? '仓库（owner/repo）' : '目录地址（HTTPS index.json）'">
          <el-input v-model="form.location" :placeholder="form.kind === 'github' ? 'your-name/your-modules' : 'https://modules.example.com/index.json'" />
        </el-form-item>
        <el-form-item v-if="form.kind === 'github'" label="分支 / 标签"><el-input v-model="form.ref" placeholder="main" /></el-form-item>
        <el-form-item label="访问令牌（可选）">
          <el-input v-model="form.token" type="password" show-password autocomplete="new-password" placeholder="私有仓库填写只读令牌；编辑时留空保留原令牌" />
        </el-form-item>
        <el-checkbox v-model="form.clearToken">清除已保存的令牌</el-checkbox>
        <p class="muted">GitHub 私有仓库令牌需要目标仓库 Contents: Read 权限。令牌仅在服务端加密保存，不会回显；修改仓库地址后需重新填写。</p>
      </el-form>
      <template #footer><el-button @click="dialog = false">取消</el-button><el-button type="primary" :loading="saving" @click="save">保存仓库</el-button></template>
    </el-dialog>
  </el-card>
</template>

<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { moduleRepositoriesApi, type ModuleRepository, type RepositoryInput, type RepositoryModule } from '@/api/moduleRepositories'
import type { ModuleOverview } from '@/api/types'
import { extractApiErrorMessage } from '@/api/client'

const props = defineProps<{ modules: ModuleOverview[]; autoRestart: boolean; activateAndEnable: boolean }>()
const emit = defineEmits<{ installed: [] }>()
const repositories = ref<ModuleRepository[]>([])
const selectedId = ref('')
const selected = computed(() => repositories.value.find(r => r.id === selectedId.value))
const catalog = ref<RepositoryModule[]>([])
const busy = ref(false)
const error = ref('')
const dialog = ref(false)
const saving = ref(false)
const editingId = ref<string | null>(null)
const form = reactive<RepositoryInput>({ name: '', kind: 'github', location: '', ref: 'main', token: '', clearToken: false })

function clearCatalog() { catalog.value = []; error.value = '' }
function isInstalled(module: RepositoryModule) { return props.modules.some(m => m.id === module.id && m.installedVersions.includes(module.version)) }
function edit(repository?: ModuleRepository) {
  editingId.value = repository?.id ?? null
  Object.assign(form, { name: repository?.name ?? '', kind: repository?.kind ?? 'github', location: repository?.location ?? '', ref: repository?.ref ?? 'main', token: '', clearToken: false })
  dialog.value = true
}
async function load() {
  repositories.value = await moduleRepositoriesApi.list()
  if (!repositories.value.some(r => r.id === selectedId.value)) selectedId.value = repositories.value[0]?.id ?? ''
}
async function save() {
  if (saving.value) return
  saving.value = true
  try {
    const repository = await moduleRepositoriesApi.save(editingId.value, { ...form })
    form.token = ''
    dialog.value = false
    selectedId.value = repository.id
    clearCatalog()
    await load()
    ElMessage.success('仓库已保存，点击连接可检查目录与访问权限')
  } finally { saving.value = false }
}
async function remove() {
  if (!selected.value) return
  try { await ElMessageBox.confirm(`移除仓库「${selected.value.name}」及其令牌？已安装模块会保留。`, '移除仓库', { type: 'warning' }) }
  catch { return }
  busy.value = true
  try { await moduleRepositoriesApi.remove(selected.value.id); clearCatalog(); await load() }
  finally { busy.value = false }
}
async function fetchCatalog() {
  if (!selected.value || busy.value) return
  busy.value = true
  clearCatalog()
  try { catalog.value = (await moduleRepositoriesApi.index(selected.value.id)).modules }
  catch (e: any) { error.value = extractApiErrorMessage(e?.response?.data) || '仓库连接失败，请检查地址、网络和令牌权限' }
  finally { busy.value = false }
}
async function install(module: RepositoryModule) {
  if (!selected.value || busy.value) return
  try { await ElMessageBox.confirm(`从「${selected.value.name}」安装 ${module.name} ${module.version}？${props.activateAndEnable ? '安装后切换并启用。' : '仅安装，不切换启用版本。'}${props.autoRestart ? '完成后会请求重启服务。' : '需手动重启后加载。'}`, '确认安装可信模块', { type: 'warning' }) }
  catch { return }
  busy.value = true
  try {
    const result = await moduleRepositoriesApi.install(selected.value.id, module, props.activateAndEnable, props.autoRestart)
    ElMessage.success(result.message)
    emit('installed')
  } finally { busy.value = false }
}
onMounted(load)
</script>

<style scoped>
.repository-toolbar { display: flex; flex-wrap: wrap; gap: 12px; align-items: center; }
.repository-toolbar strong { margin-right: auto; }
.repository-guide { margin-top: 12px; }
</style>
