<template>
  <div class="cr-fmpage">
    <header class="cr-fmpage-header">
      <h1 class="text-h6 q-ma-none">Files</h1>
      <p class="cr-fmpage-subhead">
        Browse the workspace's working tree. Open text files in Monaco; edit and save with the usual
        write pipeline (uncommitted changes show in the header's Commit badge for git workspaces).
        Right-click a row to delete; toolbar's <strong>New file</strong> creates a text file at any
        path under the current folder (subfolders auto-created). Binary uploads and drag-drop land
        later.
      </p>
    </header>
    <div class="cr-fmpage-frame">
      <FileManagerWidget />
    </div>
  </div>
</template>

<script setup lang="ts">
import FileManagerWidget from 'src/components/files/FileManagerWidget.vue';
</script>

<style lang="scss" scoped>
.cr-fmpage {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: calc(100vh - var(--cr-header-height));
}

.cr-fmpage-header {
  padding: 24px 32px 12px;
}

.cr-fmpage-subhead {
  margin: 4px 0 0;
  font-size: 12px;
  color: var(--cr-fg-secondary);
  max-width: 760px;
}

.cr-fmpage-frame {
  flex: 1;
  margin: 8px 32px 32px;
  border: 1px solid var(--cr-border-subtle);
  border-radius: 4px;
  overflow: hidden;
  display: flex;
  flex-direction: column;
}
</style>
