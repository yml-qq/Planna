// @vitest-environment jsdom
import { mount } from '@vue/test-utils'
import * as axe from 'axe-core'
import { beforeAll, describe, expect, it } from 'vitest'
import ConfirmDialog from './ConfirmDialog.vue'

beforeAll(() => {
  HTMLDialogElement.prototype.showModal = function showModal(this: HTMLDialogElement) {
    this.setAttribute('open', '')
  }
  HTMLDialogElement.prototype.close = function close(this: HTMLDialogElement) {
    this.removeAttribute('open')
  }
})

describe('ConfirmDialog accessibility', () => {
  it('has no basic axe violations while open', async () => {
    const wrapper = mount(ConfirmDialog, {
      props: {
        open: true,
        title: '删除目标',
        message: '删除后无法恢复，确定继续？',
        confirmLabel: '删除'
      },
      attachTo: document.body
    })
    await wrapper.vm.$nextTick()
    const results = await axe.run(document.body, {
      runOnly: {
        type: 'rule',
        values: ['aria-dialog-name', 'button-name', 'aria-allowed-attr']
      }
    })
    expect(results.violations).toEqual([])
    wrapper.unmount()
  })
})
