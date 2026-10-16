const followUpPanel = document.querySelector('[data-follow-up]');
if (followUpPanel) {
  const fields = [...followUpPanel.querySelectorAll('[data-follow-up-answer]')];
  const confirmation = document.getElementById('Input_FollowUpConfirmed');
  const state = document.getElementById('follow-up-state');
  const update = (edited = false) => {
    const hasAnswers = fields.some(field => field.value.trim().length > 0);
    confirmation.required = hasAnswers;
    if (edited) confirmation.checked = false;
    state.textContent = hasAnswers ? 'Confirm these answers with the citizen before preparing the draft. Leave unknown answers blank.' : '';
  };
  fields.forEach(field => field.addEventListener('input', () => update(true)));
  document.getElementById('Input_OriginalText')?.addEventListener('input', () => update(true));
  confirmation.addEventListener('invalid', () => { followUpPanel.open = true; update(); });
  update();
}
