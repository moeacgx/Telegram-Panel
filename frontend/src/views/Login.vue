<template>
  <div class="login-page">
    <el-card class="login-card" shadow="never">
      <div class="login-title">Telegram Panel</div>
      <div class="login-subtitle">管理后台</div>
      <el-alert v-if="error" :title="error" type="error" show-icon :closable="false" class="mb-3" />
      <el-form @submit.prevent="submit">
        <el-form-item>
          <el-input v-model="username" size="large" placeholder="账号" autocomplete="username" />
        </el-form-item>
        <el-form-item>
          <el-input v-model="password" size="large" placeholder="密码" type="password" show-password autocomplete="current-password" />
        </el-form-item>
        <el-button type="primary" size="large" :loading="loading" class="login-btn" @click="submit">登录</el-button>
      </el-form>
    </el-card>
  </div>
</template>

<script setup lang="ts">
import { ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useAuthStore } from '@/stores/auth'
import { extractApiErrorMessage } from '@/api/client'

const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const username = ref('')
const password = ref('')
const loading = ref(false)
const error = ref('')

async function submit() {
  error.value = ''
  loading.value = true
  try {
    await auth.login(username.value, password.value)
    if (auth.me?.mustChangePassword) {
      await router.replace({ path: '/admin/password', query: { returnUrl: '/dashboard' } })
      return
    }
    const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : '/dashboard'
    await router.replace(redirect)
  } catch (cause: any) {
    error.value = cause?.response?.status === 401
      ? '账号或密码错误'
      : extractApiErrorMessage(cause?.response?.data) || '登录失败，请稍后重试'
  } finally {
    loading.value = false
  }
}
</script>
