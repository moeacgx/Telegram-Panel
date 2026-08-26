<template>
  <el-tag :type="type" size="small">{{ label }}</el-tag>
</template>

<script setup lang="ts">
import { computed } from 'vue'

const props = defineProps<{ status: string }>()

const label = computed(() => {
  switch (props.status) {
    case 'pending': return '等待中'
    case 'running': return '执行中'
    case 'pausing': return '停止中'
    case 'paused': return '已暂停'
    case 'completed': return '已完成'
    case 'failed': return '失败'
    case 'canceled': return '已取消'
    case 'enabled': return '启用中'
    default: return props.status || '-'
  }
})

const type = computed(() => {
  switch (props.status) {
    case 'running': return 'primary'
    case 'pausing': return 'warning'
    case 'completed': return 'success'
    case 'failed': return 'danger'
    case 'paused': return 'warning'
    case 'canceled': return 'info'
    case 'enabled': return 'success'
    default: return 'info'
  }
})
</script>
