<template>
  <div class="admin-password-page">
    <el-card shadow="never" class="page-card password-card">
      <template #header>后台账号安全</template>

      <el-alert
        v-if="auth.me?.mustChangePassword"
        title="当前仍为初始密码，为安全起见请立即修改。"
        type="warning"
        :closable="false"
        show-icon
        class="mb-3"
      />

      <el-alert
        v-if="auth.me && !auth.me.authEnabled"
        title="后台验证未启用"
        type="warning"
        :closable="false"
        show-icon
        class="mb-3"
      />

      <div class="current-user">当前后台用户名：{{ auth.me?.username || 'admin' }}</div>

      <el-form label-position="top">
        <el-form-item label="新后台用户名">
          <el-input
            v-model="usernameForm.newUsername"
            autocomplete="off"
            placeholder="4-32 位，建议不要使用 admin/root 等常见名称"
            @keyup.enter="saveUsername"
          />
          <div class="muted mt-2">修改后会立即更新当前登录会话；下次登录需使用新用户名。</div>
        </el-form-item>
        <el-form-item label="当前密码（用于确认修改用户名）">
          <el-input v-model="usernameForm.currentPassword" type="password" show-password autocomplete="current-password" @keyup.enter="saveUsername" />
        </el-form-item>
      </el-form>

      <div class="button-row">
        <el-button type="primary" plain :loading="savingUsername" @click="saveUsername">保存用户名</el-button>
      </div>

      <el-divider />

      <el-form label-position="top">
        <el-form-item label="当前密码">
          <el-input v-model="passwordForm.currentPassword" type="password" show-password autocomplete="current-password" />
        </el-form-item>
        <el-form-item label="新密码">
          <el-input v-model="passwordForm.newPassword" type="password" show-password autocomplete="new-password" />
        </el-form-item>
        <el-form-item label="确认新密码">
          <el-input v-model="passwordForm.confirmPassword" type="password" show-password autocomplete="new-password" @keyup.enter="savePassword" />
        </el-form-item>
      </el-form>

      <div class="button-row">
        <el-button type="primary" :loading="savingPassword" @click="savePassword">保存密码</el-button>
      </div>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ElMessage } from 'element-plus'
import { panelApi } from '@/api/panel'
import { useAuthStore } from '@/stores/auth'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const savingUsername = ref(false)
const savingPassword = ref(false)
const usernameForm = reactive({
  currentPassword: '',
  newUsername: '',
})
const passwordForm = reactive({
  currentPassword: '',
  newPassword: '',
  confirmPassword: '',
})

onMounted(async () => {
  if (!auth.me) await auth.fetchMe()
  usernameForm.newUsername = auth.me?.username || ''
})

function ensureAuthEnabled() {
  if (!auth.me?.authEnabled) {
    ElMessage.warning('后台验证未启用')
    return false
  }
  return true
}

async function saveUsername() {
  if (!ensureAuthEnabled()) return
  if (!usernameForm.newUsername.trim()) {
    ElMessage.warning('请输入新后台用户名')
    return
  }
  if (!usernameForm.currentPassword.trim()) {
    ElMessage.warning('请输入当前密码')
    return
  }

  savingUsername.value = true
  try {
    const result = await panelApi.changeAdminUsername({
      currentPassword: usernameForm.currentPassword,
      newUsername: usernameForm.newUsername,
    })
    usernameForm.currentPassword = ''
    await auth.fetchMe()
    usernameForm.newUsername = auth.me?.username || usernameForm.newUsername
    ElMessage.success(result.message || '用户名已更新')
  } finally {
    savingUsername.value = false
  }
}

async function savePassword() {
  if (!ensureAuthEnabled()) return
  if (!passwordForm.currentPassword.trim()) {
    ElMessage.warning('请输入当前密码')
    return
  }
  if (passwordForm.newPassword.trim().length < 12) {
    ElMessage.warning('新密码至少 12 位，且不能与当前密码相同')
    return
  }
  if (passwordForm.newPassword !== passwordForm.confirmPassword) {
    ElMessage.warning('两次输入的新密码不一致')
    return
  }

  savingPassword.value = true
  try {
    const result = await panelApi.changeAdminPassword({
      currentPassword: passwordForm.currentPassword,
      newPassword: passwordForm.newPassword,
    })
    passwordForm.currentPassword = ''
    passwordForm.newPassword = ''
    passwordForm.confirmPassword = ''
    await auth.fetchMe()
    ElMessage.success(result.message || '密码已更新')
    const returnUrl = typeof route.query.returnUrl === 'string' && route.query.returnUrl.startsWith('/') ? route.query.returnUrl : '/dashboard'
    await router.replace(returnUrl)
  } finally {
    savingPassword.value = false
  }
}
</script>

<style scoped>
.admin-password-page {
  min-width: 0;
}

.password-card {
  max-width: 720px;
}

.current-user {
  margin-bottom: 14px;
  color: var(--tp-muted);
}

.muted {
  color: var(--tp-muted);
  font-size: 13px;
}
</style>
