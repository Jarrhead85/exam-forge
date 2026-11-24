window.examForgeDialog = {
    show(dialog) {
        if (dialog instanceof HTMLDialogElement && !dialog.open) dialog.showModal();
    },
    close(dialog) {
        if (dialog instanceof HTMLDialogElement && dialog.open) dialog.close();
    }
};
