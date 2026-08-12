<template>
  <el-container :class="['layout', { 'embed-layout': isEmbedMode }]">
    <el-header v-if="!isEmbedMode" class="appbar">
      <el-button link class="appbar-icon" @click="toggleMenu">
        <span class="material-icons">menu</span>
      </el-button>
      <div class="app-title">Telegram Panel</div>
      <el-tag
        v-if="version"
        size="small"
        effect="plain"
        class="version-chip clickable"
        title="点击查看版本信息"
        @click="openVersionDialog"
      >
        v{{ version }}
      </el-tag>
      <el-tag
        v-if="versionInfo?.success && versionInfo.updateAvailable && versionInfo.latestVersion"
        size="small"
        type="warning"
        effect="dark"
        class="version-chip clickable"
        title="发现新版本，点击查看更新说明"
        @click="openVersionDialog"
      >
        新版本 v{{ versionInfo.latestVersion }}
      </el-tag>
      <div class="appbar-spacer" />
      <el-button link class="appbar-icon appbar-secondary" title="重启面板" :disabled="restartPanelLoading" @click="restartPanel">
        <span class="material-icons">{{ restartPanelLoading ? 'hourglass_empty' : 'restart_alt' }}</span>
      </el-button>
      <el-button link class="appbar-icon appbar-secondary" title="系统设置" @click="router.push('/settings')">
        <span class="material-icons">settings</span>
      </el-button>
      <el-button link class="appbar-icon appbar-secondary" title="GitHub" @click="openGithub">
        <span class="material-icons">link</span>
      </el-button>
      <el-button link class="appbar-icon" :title="isDark ? '切换到白天模式' : '切换到黑夜模式'" @click="toggleTheme">
        <span class="material-icons">{{ isDark ? 'light_mode' : 'dark_mode' }}</span>
      </el-button>
      <el-dropdown @command="onCommand">
        <span class="user-menu">
          <el-avatar :size="28">{{ auth.me?.username?.[0]?.toUpperCase() || 'A' }}</el-avatar>
          <span v-if="!isMobile">{{ auth.me?.username || 'admin' }}</span>
          <span class="material-icons user-arrow">keyboard_arrow_down</span>
        </span>
        <template #dropdown>
          <el-dropdown-menu>
            <el-dropdown-item command="logout">退出登录</el-dropdown-item>
          </el-dropdown-menu>
        </template>
      </el-dropdown>
    </el-header>

    <el-container class="shell">
      <el-aside v-if="!isEmbedMode && !isMobile" :width="collapsed ? '72px' : '256px'" class="aside">
      <el-menu
        :collapse="collapsed"
        :default-active="activeIndex"
        :default-openeds="defaultOpeneds"
        background-color="transparent"
        :text-color="menuTextColor"
        :active-text-color="menuActiveTextColor"
        class="menu"
        @select="handleSelect"
      >
        <template v-for="item in menuItems" :key="item.index">
          <el-menu-item v-if="!item.children" :index="item.index">
            <MenuIcon :icon="item.icon" />
            <template #title>{{ item.label }}</template>
          </el-menu-item>
          <el-sub-menu v-else :index="item.index">
            <template #title>
              <MenuIcon :icon="item.icon" />
              <span>{{ item.label }}</span>
            </template>
            <el-menu-item v-for="child in item.children" :key="child.index" :index="child.index">
              <MenuIcon :icon="child.icon" />
              <template #title>{{ child.label }}</template>
            </el-menu-item>
          </el-sub-menu>
        </template>
      </el-menu>
    </el-aside>

    <el-drawer v-if="!isEmbedMode && isMobile" v-model="drawerOpen" direction="ltr" :with-header="false" size="256px">
      <div class="mobile-title">Telegram Panel</div>
      <el-menu
        :default-active="activeIndex"
        :default-openeds="defaultOpeneds"
        background-color="transparent"
        :text-color="menuTextColor"
        :active-text-color="menuActiveTextColor"
        @select="handleMobileSelect"
      >
        <template v-for="item in menuItems" :key="item.index">
          <el-menu-item v-if="!item.children" :index="item.index">
            <MenuIcon :icon="item.icon" />
            <template #title>{{ item.label }}</template>
          </el-menu-item>
          <el-sub-menu v-else :index="item.index">
            <template #title>
              <MenuIcon :icon="item.icon" />
              <span>{{ item.label }}</span>
            </template>
            <el-menu-item v-for="child in item.children" :key="child.index" :index="child.index">
              <MenuIcon :icon="child.icon" />
              <template #title>{{ child.label }}</template>
            </el-menu-item>
          </el-sub-menu>
        </template>
      </el-menu>
    </el-drawer>

    <el-container>
      <el-main class="main">
        <div v-if="!isEmbedMode" class="page-title">{{ pageTitle }}</div>
        <router-view />
      </el-main>
    </el-container>
    </el-container>
  </el-container>

  <el-dialog v-model="versionDialog.visible" title="版本信息" width="680px" class="version-dialog">
    <div v-loading="versionDialog.loading" class="version-dialog-body">
      <div class="version-title">Telegram Panel</div>
      <el-descriptions :column="1" border size="small">
        <el-descriptions-item label="版本">v{{ versionInfo?.currentVersion || version || '-' }}</el-descriptions-item>
        <el-descriptions-item label="运行环境">{{ versionInfo?.isDocker ? 'Docker' : '非 Docker' }}</el-descriptions-item>
        <el-descriptions-item label="更新方式">
          <el-radio-group
            v-model="versionDialog.updateMode"
            size="small"
            :disabled="versionDialog.modeSaving"
            @change="saveUpdateMode"
          >
            <el-radio-button label="auto">自动选择</el-radio-button>
            <el-radio-button label="image">Docker 镜像</el-radio-button>
            <el-radio-button label="binary">容器内二进制</el-radio-button>
          </el-radio-group>
        </el-descriptions-item>
      </el-descriptions>

      <el-link class="mt-3" type="primary" href="https://github.com/moeacgx/Telegram-Panel" target="_blank">
        GitHub 仓库
      </el-link>

      <el-divider />

      <div class="version-actions">
        <div class="section-title">检查更新</div>
        <div class="action-buttons">
          <el-button :loading="versionDialog.checking" @click="refreshVersionInfo(true)">立即检查</el-button>
          <el-button
            type="warning"
            :disabled="!canApplyVersionUpdate"
            :loading="versionDialog.updating"
            @click="applyVersionUpdate"
          >
            一键更新并重启
          </el-button>
        </div>
      </div>

      <el-alert
        v-if="versionInfo && !versionInfo.success"
        class="mt-3"
        type="warning"
        :title="`检查失败：${versionInfo.error || '-'}`"
        :closable="false"
        show-icon
      />
      <div v-else-if="versionInfo && !versionInfo.enabled" class="muted mt-3">已禁用自动更新</div>
      <el-alert
        v-else-if="versionInfo?.updateAvailable"
        class="mt-3"
        type="info"
        :closable="false"
        show-icon
      >
        <template #title>
          发现新版本：v{{ versionInfo.latestVersion }}（当前 v{{ versionInfo.currentVersion }}）
        </template>
        <el-link v-if="versionInfo.url" type="primary" :href="versionInfo.url" target="_blank">查看发布页</el-link>
      </el-alert>
      <div v-else-if="versionInfo" class="muted mt-3">当前已是最新版本（v{{ versionInfo.currentVersion }}）</div>

      <div v-if="versionInfo?.assetName" class="muted mt-3">
        匹配更新包：{{ versionInfo.assetName }}<span v-if="versionInfo.assetSizeBytes">（{{ formatBytes(versionInfo.assetSizeBytes) }}）</span>
      </div>

      <el-alert
        v-if="versionInfo?.blockedReason"
        class="mt-3"
        type="warning"
        :title="`无法一键更新：${versionInfo.blockedReason}`"
        :closable="false"
        show-icon
      />

      <template v-if="versionInfo?.notes">
        <div class="section-title mt-4">Release Notes / 更新内容</div>
        <pre class="release-notes">{{ versionInfo.notes }}</pre>
      </template>

      <div v-if="versionInfo" class="muted mt-3">最近检查：{{ formatTime(versionInfo.checkedAtUtc) }}</div>
    </div>
    <template #footer>
      <el-button @click="versionDialog.visible = false">关闭</el-button>
    </template>
  </el-dialog>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { panelApi } from '@/api/panel'
import type { ModuleNavItem, UpdateMode, VersionInfo } from '@/api/types'
import { formatTime } from '@/utils/format'
import { ElMessage, ElMessageBox } from 'element-plus'
import MenuIcon from '@/components/MenuIcon.vue'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const collapsed = ref(false)
const drawerOpen = ref(false)
const isMobile = ref(window.innerWidth < 780)
const moduleNavItems = ref<ModuleNavItem[]>([])
const versionInfo = ref<VersionInfo | null>(null)
const version = computed(() => versionInfo.value?.currentVersion || auth.me?.version || '')
const isDark = ref(false)
const restartPanelLoading = ref(false)
const versionDialog = ref({
  visible: false,
  loading: false,
  checking: false,
  updating: false,
  modeSaving: false,
  updateMode: 'auto' as UpdateMode,
})

const pageTitle = computed(() => (route.meta.title as string) || '')
const activeIndex = computed(() => (route.path === '/dictionaries' ? '/data-dictionaries' : route.path))
const isEmbedMode = computed(() => route.query.embed === '1')
const defaultOpeneds: string[] = []
const menuTextColor = computed(() => (isDark.value ? '#c6cad4' : '#3f4b5b'))
const menuActiveTextColor = computed(() => (isDark.value ? '#90caf9' : '#1976d2'))
const canApplyVersionUpdate = computed(() =>
  versionInfo.value?.success === true
  && versionInfo.value.enabled
  && versionInfo.value.updateAvailable
  && versionInfo.value.canApply,
)

interface MenuItem {
  index: string
  label: string
  icon: string
  external?: boolean
  children?: MenuItem[]
}

const staticMenuItems: MenuItem[] = [
  { index: '/dashboard', label: '仪表盘', icon: 'dashboard' },
  {
    index: 'accounts-group',
    label: '账号管理',
    icon: 'account_circle',
    children: [
      { index: '/accounts', label: '账号列表', icon: 'people' },
      { index: '/accounts/import', label: '导入账号', icon: 'upload' },
      { index: '/accounts/login', label: '手动登录', icon: 'login' },
      { index: '/accounts/categories', label: '账号分类', icon: 'category' },
    ],
  },
  { index: '/proxies', label: '代理管理', icon: 'vpn_lock' },
  {
    index: 'channels-group',
    label: '频道管理',
    icon: 'campaign',
    children: [
      { index: '/channels', label: '频道列表', icon: 'list' },
      { index: '/channels/create', label: '创建频道', icon: 'add' },
      { index: '/channels/groups', label: '频道分类', icon: 'folder' },
    ],
  },
  {
    index: 'groups-group',
    label: '群组管理',
    icon: 'group',
    children: [
      { index: '/groups', label: '群组列表', icon: 'list' },
      { index: '/groups/create', label: '创建群组', icon: 'add' },
      { index: '/groups/categories', label: '群组分类', icon: 'folder' },
    ],
  },
  {
    index: 'bots-group',
    label: '机器人管理',
    icon: 'smart_toy',
    children: [
      { index: '/bots', label: '机器人列表', icon: 'smart_toy' },
      { index: '/bots/channels', label: 'Bot 频道', icon: 'list' },
    ],
  },
  { index: '/tasks', label: '任务中心', icon: 'assignment' },
  { index: '/data-dictionaries', label: '数据字典', icon: 'menu_book' },
  { index: '/modules', label: '模块管理', icon: 'extension' },
  { index: '/apis', label: 'API 管理', icon: 'link' },
  { index: '/settings', label: '系统设置', icon: 'settings' },
  { index: 'logout', label: '退出登录', icon: 'logout' },
]

const menuItems = computed<MenuItem[]>(() => {
  const items = [...staticMenuItems]
  const mergedModuleItems = new Map<string, ModuleNavItem>()

  for (const item of moduleNavItems.value) {
    const href = normalizeModuleHref(item.href)
    if (!href || href === '/modules') continue
    mergedModuleItems.set(href, item)
  }

  const extensionChildren = Array.from(mergedModuleItems.values())
    .sort((a, b) => (a.group || '').localeCompare(b.group || '', 'zh-Hans-CN') || a.order - b.order || a.title.localeCompare(b.title, 'zh-Hans-CN'))
    .map((item) => ({
      index: resolveModuleRoute(item),
      label: item.title,
      icon: resolveModuleIcon(item),
    }))

  if (extensionChildren.length > 0) {
    const moduleIndex = items.findIndex((x) => x.index === '/modules')
    items.splice(moduleIndex + 1, 0, {
      index: 'extensions-group',
      label: '扩展模块',
      icon: 'extension',
      children: extensionChildren,
    })
  }

  return items
})

function onResize() {
  isMobile.value = window.innerWidth < 780
}

function toggleMenu() {
  if (isMobile.value) drawerOpen.value = true
  else collapsed.value = !collapsed.value
}

function openGithub() {
  window.open('https://github.com/moeacgx/Telegram-Panel', '_blank', 'noopener,noreferrer')
}

function loadStoredTheme() {
  return localStorage.getItem('telegram-panel-theme') === 'dark'
}

function applyTheme(dark: boolean) {
  isDark.value = dark
  document.documentElement.classList.toggle('dark', dark)
}

function toggleTheme() {
  applyTheme(!isDark.value)
  localStorage.setItem('telegram-panel-theme', isDark.value ? 'dark' : 'light')
}

function handleSelect(index: string) {
  if (index === 'logout') {
    auth.logout()
    return
  }

  if (index.startsWith('direct:')) {
    window.location.href = index.slice('direct:'.length)
    return
  }

  router.push(index)
}

function handleMobileSelect(index: string) {
  drawerOpen.value = false
  handleSelect(index)
}

function onCommand(command: string) {
  if (command === 'logout') auth.logout()
}

onMounted(() => {
  applyTheme(loadStoredTheme())
  window.addEventListener('resize', onResize)
  void loadModuleNav()
})
onUnmounted(() => window.removeEventListener('resize', onResize))

function normalizeModuleHref(href: string) {
  if (!href) return '/modules'
  if (href.startsWith('/ui/')) return href.slice(3) || '/'
  if (href.startsWith('/')) return href
  return `/${href}`
}

function resolveModuleRoute(item: ModuleNavItem) {
  const pageKey = (item.pageKey || '').trim()
  if (item.moduleId && pageKey) {
    return `/ext/${encodeURIComponent(item.moduleId)}/${encodeURIComponent(pageKey)}`
  }

  const href = normalizeModuleHref(item.href)
  const match = href.match(/^\/ext\/([^/?#]+)\/([^/?#]+)/i)
  if (match) {
    try {
      return `/ext/${encodeURIComponent(decodeURIComponent(match[1]))}/${encodeURIComponent(decodeURIComponent(match[2]))}`
    } catch {
      // 损坏的百分号编码不能影响整个主布局，交由原始模块地址处理。
    }
  }

  if (item.uiMode === 'direct') {
    return `direct:${href}`
  }

  return href
}

function resolveModuleIcon(item: ModuleNavItem) {
  return (item.icon || '').trim() || 'extension'
}

async function loadModuleNav() {
  try {
    moduleNavItems.value = await panelApi.moduleNav()
  } catch {
    moduleNavItems.value = []
  }
}

async function openVersionDialog() {
  versionDialog.value.visible = true
  if (!versionInfo.value) await refreshVersionInfo(false)
}

async function refreshVersionInfo(forceRefresh: boolean) {
  if (forceRefresh) versionDialog.value.checking = true
  else if (versionDialog.value.visible) versionDialog.value.loading = true

  try {
    versionInfo.value = forceRefresh ? await panelApi.checkVersionInfo() : await panelApi.versionInfo()
    versionDialog.value.updateMode = versionInfo.value.updateMode || 'auto'
  } catch {
    if (forceRefresh || versionDialog.value.visible) ElMessage.warning('检查版本信息失败')
  } finally {
    versionDialog.value.checking = false
    versionDialog.value.loading = false
  }
}

async function saveUpdateMode(mode: UpdateMode) {
  if (versionDialog.value.modeSaving) return

  versionDialog.value.modeSaving = true
  try {
    const result = await panelApi.updateVersionMode(mode)
    versionDialog.value.updateMode = result.mode
    ElMessage.success(result.message)
    await refreshVersionInfo(true)
    ElMessage.warning('更新方式已保存，重启面板后生效')
  } catch {
    await refreshVersionInfo(true)
    ElMessage.error('更新方式保存失败')
  } finally {
    versionDialog.value.modeSaving = false
  }
}

async function applyVersionUpdate() {
  if (!canApplyVersionUpdate.value) {
    ElMessage.warning(versionInfo.value?.blockedReason || '当前条件不满足一键更新')
    return
  }

  const target = versionInfo.value?.latestTag || versionInfo.value?.latestVersion || '最新版本'
  await ElMessageBox.confirm(`将升级到 ${target}，并在部署后自动重启服务。是否继续？`, '确认一键更新', {
    type: 'warning',
    confirmButtonText: '继续',
    cancelButtonText: '取消',
  })

  versionDialog.value.updating = true
  try {
    const result = await panelApi.applyVersionUpdate()
    ElMessage.success(result.message || '更新已提交')
    versionDialog.value.visible = false
    await refreshVersionInfo(true)
  } finally {
    versionDialog.value.updating = false
  }
}

async function restartPanel() {
  if (restartPanelLoading.value) return

  await ElMessageBox.confirm(
    '将请求面板服务重启。Docker、系统服务或桌面版守护进程会自动拉起；如果是直接运行二进制，需要由外部守护负责重新启动。是否继续？',
    '确认重启面板',
    {
      type: 'warning',
      confirmButtonText: '重启',
      cancelButtonText: '取消',
    },
  )

  restartPanelLoading.value = true
  try {
    const result = await panelApi.restartPanel()
    ElMessage.success(result.message || '已提交重启请求')
    window.setTimeout(() => {
      window.location.reload()
    }, 8000)
  } finally {
    window.setTimeout(() => {
      restartPanelLoading.value = false
    }, 10000)
  }
}

function formatBytes(bytes: number) {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit += 1
  }
  return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`
}
</script>
