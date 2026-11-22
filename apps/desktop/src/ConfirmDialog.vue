<script setup lang="ts">
import { nextTick, ref, watch } from 'vue'

const props = defineProps<{
  open: boolean
  title: string
  message: string
  confirmLabel?: string
}>()

const emit = defineEmits<{ confirm: []; cancel: [] }>()
const dialog = ref<HTMLDialogElement | null>(null)

watch(
  () => props.open,
  async (open) => {
    await nextTick()
    if (!dialog.value) return
    if (open && !dialog.value.open) dialog.value.showModal()
    else if (!open && dialog.value.open) dialog.value.close()
  },
  { immediate: true }
)

function onCancel(event: Event): void {
  event.preventDefault()
  emit('cancel')
}
</script>

<template>
  <dialog
    ref="dialog"
    class="confirm"
    aria-modal="true"
    aria-labelledby="confirm-title"
    aria-describedby="confirm-message"
    @cancel="onCancel"
    @close="emit('cancel')"
  >
    <h2 id="confirm-title" class="card__title">{{ title }}</h2>
    <p id="confirm-message">{{ message }}</p>
    <div class="composer__row">
      <button class="button button--ghost" type="button" autofocus @click="emit('cancel')">
        取消
      </button>
      <button class="button" type="button" @click="emit('confirm')">
        {{ confirmLabel ?? '确认' }}
      </button>
    </div>
  </dialog>
</template>
