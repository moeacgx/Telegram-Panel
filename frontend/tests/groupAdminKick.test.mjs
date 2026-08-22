import assert from 'node:assert/strict'
import { readFile } from 'node:fs/promises'
import test from 'node:test'

const panelSource = await readFile(new URL('../src/api/panel.ts', import.meta.url), 'utf8')
const chatResourcesSource = await readFile(new URL('../src/views/ChatResources.vue', import.meta.url), 'utf8')

test('群组详情为非创建者管理员提供专用踢出操作', () => {
  assert.match(panelSource, /kickGroupAdmin: \(id: number, userId: number\)/)
  assert.match(panelSource, /`\/groups\/\$\{id\}\/admins\/\$\{userId\}\/kick`/)
  assert.match(chatResourcesSource, /<el-table-column v-if="kind === 'group'" label="操作" width="72" align="center">/)
  assert.match(chatResourcesSource, /<el-tooltip v-if="!row\.isCreator" content="踢出管理员"/)
  assert.match(chatResourcesSource, /:disabled="detail\.adminKickLoadingGroupId !== null"/)
  assert.match(chatResourcesSource, /:icon="Delete"/)
})

test('踢出管理员需确认，串行执行并只刷新发起操作的详情', () => {
  assert.match(chatResourcesSource, /async function kickAdminFromDetail\(admin: ChatAdmin\)/)
  assert.match(chatResourcesSource, /const groupId = detail\.row\?\.id/)
  assert.match(chatResourcesSource, /确定踢出管理员「\$\{admin\.displayName\}」吗？系统会先撤销管理员权限，再将其移出群组。/)
  assert.match(chatResourcesSource, /await panelApi\.kickGroupAdmin\(groupId, admin\.userId\)/)
  assert.match(chatResourcesSource, /await Promise\.allSettled\(\[\s*loadDetailAdmins\(groupId\),\s*loadDetailAccounts\(groupId\),/)
  assert.match(chatResourcesSource, /if \(detail\.row\?\.id === resourceId\) detail\.admins = admins/)
})
