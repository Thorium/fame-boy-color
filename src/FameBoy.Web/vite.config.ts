import { defineConfig } from 'vite'

function normalizeBasePath(basePath?: string) {
  if (!basePath || basePath === '/') {
    return '/'
  }

  const withLeadingSlash = basePath.startsWith('/') ? basePath : `/${basePath}`
  return withLeadingSlash.endsWith('/') ? withLeadingSlash : `${withLeadingSlash}/`
}

const repositoryName = process.env.GITHUB_REPOSITORY?.split('/')[1]
const base = normalizeBasePath(process.env.PAGES_BASE_PATH ?? repositoryName)

// https://vitejs.dev/config/
export default defineConfig({
  base,
  clearScreen: false,
  server: {
    watch: {
      ignored: [
        "**/*.fs" // Don't watch F# files
      ]
    }
  }
})
