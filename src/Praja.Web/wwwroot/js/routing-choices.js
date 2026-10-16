const routingChoices = document.querySelector('[data-routing-choices]');
if (routingChoices) {
  const department = document.getElementById('Department');
  const category = document.getElementById('CategoryCode');
  const issue = document.getElementById('SubcategoryCode');
  const reviewed = document.getElementById('Reviewed');
  const state = document.getElementById('routing-choice-state');
  const categories = [...category.options].map(option => option.cloneNode(true));
  const issues = [...issue.options].map(option => option.cloneNode(true));
  const fill = (select, templates, predicate, keep) => {
    const options = templates.filter(option => !option.value || predicate(option)).map(option => {
      const copy = option.cloneNode(true); if (copy.dataset.label) copy.textContent = copy.dataset.label; return copy;
    });
    select.replaceChildren(...options);
    select.value = options.some(option => option.value === keep) ? keep : '';
  };
  const refresh = (keepCategory, keepIssue) => {
    fill(category, categories, option => option.dataset.department === department.value, keepCategory);
    fill(issue, issues, option => option.dataset.category === category.value, keepIssue);
  };
  const changed = () => { reviewed.checked = false; state.textContent = 'Review the selected service, category and issue before confirming registration.'; };
  department.addEventListener('change', () => { refresh('', ''); changed(); });
  category.addEventListener('change', () => { fill(issue, issues, option => option.dataset.category === category.value, ''); changed(); });
  issue.addEventListener('change', changed);
  refresh(category.value, issue.value);
}
