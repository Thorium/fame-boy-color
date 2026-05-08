import { defineConfig } from 'vite'
// https://vitejs.dev/config/
export default defineConfig({
  base: '/fame-boy-color/',
  clearScreen: false,
  server: {
    watch: {
      ignored: [
        "**/*.fs" // Don't watch F# files
      ]
    }
  }
})
