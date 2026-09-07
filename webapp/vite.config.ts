import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Served from the Function App at /api/app, so all asset URLs are prefixed accordingly.
// The build output goes straight into the function project's wwwroot folder.
export default defineConfig({
  base: '/api/app/',
  plugins: [react()],
  build: {
    outDir: '../BcNuGetHelper/wwwroot',
    emptyOutDir: true,
  },
});
