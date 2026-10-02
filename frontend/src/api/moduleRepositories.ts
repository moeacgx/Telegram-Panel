import { api } from './client'

export interface ModuleRepository {
  id: string
  name: string
  kind: 'github' | 'https'
  location: string
  ref: string
  hasToken: boolean
}
export interface RepositoryModule {
  id: string
  name: string
  version: string
  downloadUrl: string
  sha256: string
  description?: string
}
export interface RepositoryInput {
  name: string
  kind: 'github' | 'https'
  location: string
  ref: string
  token: string
  clearToken: boolean
}
export interface PrunePreview { id: string; versions: string[]; keptVersions: string[] }
export const moduleRepositoriesApi = {
  list: () => api.get<ModuleRepository[]>('/module-repositories').then(r => r.data),
  save: (id: string | null, input: RepositoryInput) => (id
    ? api.put<ModuleRepository>(`/module-repositories/${encodeURIComponent(id)}`, input)
    : api.post<ModuleRepository>('/module-repositories', input)).then(r => r.data),
  remove: (id: string) => api.delete(`/module-repositories/${encodeURIComponent(id)}`),
  index: (id: string) => api.get<{ modules: RepositoryModule[] }>(`/module-repositories/${encodeURIComponent(id)}/index`, { timeout: 50_000 }).then(r => r.data),
  install: (id: string, module: RepositoryModule, activateAndEnable: boolean, autoRestart: boolean) =>
    api.post<{ success: boolean; message: string }>(`/module-repositories/${encodeURIComponent(id)}/install`, {
      moduleId: module.id, version: module.version, sha256: module.sha256, activateAndEnable, autoRestart,
    }, { timeout: 120_000 }).then(r => r.data),
  prunePreview: () => api.get<PrunePreview[]>('/modules/prune-preview').then(r => r.data),
  pruneAll: (autoRestart: boolean) => api.post<{
    success: boolean; removedVersions: number
    results: { id: string; version: string; success: boolean; message: string }[]
  }>('/modules/prune-all', { autoRestart }, { timeout: 120_000 }).then(r => r.data),
}
