// Custom JavaScript to make Inherited Members collapsible

export default {
  start: () => {
    makeInheritedMembersCollapsible();
  },
}

function makeInheritedMembersCollapsible() {
  const inheritedMembersDl = document.querySelector('dl.inheritedMembers');
  
  if (!inheritedMembersDl) return;
  
  const dt = inheritedMembersDl.querySelector('dt');
  const dd = inheritedMembersDl.querySelector('dd');
  
  if (!dt || !dd) return;
  
  const memberDivs = dd.querySelectorAll(':scope > div');
  const memberCount = memberDivs.length;
  
  if (memberCount === 0) return;
  
  const wrapper = document.createElement('div');
  wrapper.className = 'inheritedMembers-section';
  
  const details = document.createElement('details');
  
  const summary = document.createElement('summary');
  summary.innerHTML = `${dt.textContent} <span class="member-count">(${memberCount} members)</span>`;
  
  const content = document.createElement('div');
  content.className = 'content';
  
  content.appendChild(dd.cloneNode(true));
  
  details.appendChild(summary);
  details.appendChild(content);
  wrapper.appendChild(details);
  
  inheritedMembersDl.parentNode.insertBefore(wrapper, inheritedMembersDl);
  inheritedMembersDl.remove();
}
