import * as React from "react";
import { Icon, Toggle } from "@fluentui/react";
export const Page: React.FC<{
    wide?: boolean;
    children: React.ReactNode;
}> = ({ wide, children }) => (<div className={wide ? "pccfg-page is-wide" : "pccfg-page"}>{children}</div>);
export const PageHeader: React.FC<{
    title: string;
    subtitle?: string;
    actions?: React.ReactNode;
}> = ({ title, subtitle, actions }) => (<div className="pccfg-page-head">
    <div>
      <div className="pccfg-page-title">{title}</div>
      {subtitle && <div className="pccfg-page-sub">{subtitle}</div>}
    </div>
    <div className="pccfg-spacer"/>
    {actions}
  </div>);
export const Card: React.FC<{
    icon?: string;
    title?: string;
    subtitle?: string;
    actions?: React.ReactNode;
    tight?: boolean;
    flush?: boolean;
    bodyClassName?: string;
    children: React.ReactNode;
}> = ({ icon, title, subtitle, actions, tight, flush, bodyClassName, children }) => (<div className={flush ? "pccfg-card is-flush" : "pccfg-card"}>
    {title && (<div className="pccfg-card-head">
        {icon && <div className="pccfg-card-icon"><Icon iconName={icon}/></div>}
        <div>
          <div className="pccfg-card-title">{title}</div>
          {subtitle && <div className="pccfg-card-sub">{subtitle}</div>}
        </div>
        {actions && <><div className="pccfg-spacer"/>{actions}</>}
      </div>)}
    <div className={["pccfg-card-body", tight ? "is-tight" : "", bodyClassName ?? ""].filter(Boolean).join(" ")}>
      {children}
    </div>
  </div>);
export const FieldGrid: React.FC<{
    single?: boolean;
    children: React.ReactNode;
}> = ({ single, children }) => (<div className={single ? "pccfg-grid is-single" : "pccfg-grid"}>{children}</div>);
export const Field: React.FC<{
    span?: boolean;
    hint?: string;
    children: React.ReactNode;
}> = ({ span, hint, children }) => (<div className={span ? "pccfg-span" : undefined}>
    {children}
    {hint && <div className="pccfg-hint">{hint}</div>}
  </div>);
export const SwitchRow: React.FC<{
    title: string;
    description?: string;
    checked: boolean;
    disabled?: boolean;
    onChange: (checked: boolean) => void;
}> = ({ title, description, checked, disabled, onChange }) => (<div className="pccfg-switch">
    <div className="pccfg-switch-text">
      <div className="pccfg-switch-title">{title}</div>
      {description && <div className="pccfg-switch-sub">{description}</div>}
    </div>
    <Toggle checked={checked} disabled={disabled} onChange={(_, c) => onChange(!!c)}/>
  </div>);
export const SwitchList: React.FC<{
    children: React.ReactNode;
}> = ({ children }) => (<div className="pccfg-switches">{children}</div>);
export const SubHeader: React.FC<{
    children: React.ReactNode;
}> = ({ children }) => (<div className="pccfg-sub-head">{children}</div>);
export const EmptyState: React.FC<{
    icon?: string;
    title: string;
    detail?: string;
}> = ({ icon = "Manufacturing", title, detail }) => (<div className="pccfg-empty">
    <div className="pccfg-empty-icon"><Icon iconName={icon}/></div>
    <div className="pccfg-empty-title">{title}</div>
    {detail && <div className="pccfg-empty-sub">{detail}</div>}
  </div>);
