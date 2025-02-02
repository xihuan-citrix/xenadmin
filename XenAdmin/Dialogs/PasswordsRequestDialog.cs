﻿/* Copyright (c) Citrix Systems, Inc. 
 * All rights reserved. 
 * 
 * Redistribution and use in source and binary forms, 
 * with or without modification, are permitted provided 
 * that the following conditions are met: 
 * 
 * *   Redistributions of source code must retain the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer. 
 * *   Redistributions in binary form must reproduce the above 
 *     copyright notice, this list of conditions and the 
 *     following disclaimer in the documentation and/or other 
 *     materials provided with the distribution. 
 * 
 * THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND 
 * CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, 
 * INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF 
 * MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE 
 * DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR 
 * CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, 
 * SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, 
 * BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR 
 * SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS 
 * INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, 
 * WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING 
 * NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE 
 * OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF 
 * SUCH DAMAGE.
 */

using System;
using System.Drawing;
using System.Windows.Forms;
using XenAdmin.Core;


namespace XenAdmin.Dialogs
{
    /// <summary>
    /// The result from ShowDialog() will be DialogResult.OK for "Give Passwords This Time", DialogResult.Yes
    /// for "Give Passwords Always", or DialogResult.Cancel.
    /// </summary>
    public partial class PasswordsRequestDialog : XenDialogBase
    {
        public PasswordsRequestDialog()
        {
            InitializeComponent();
            label1.Text = string.Format(label1.Text, BrandManager.BrandConsole);
            bool requirePass = Properties.Settings.Default.RequirePass;
            OKAlwaysButton.Enabled = !requirePass;
            tableLayoutPanel2.Visible = requirePass;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            Text = BrandManager.BrandConsole;
        }

        public string Application
        {
            set => ApplicationLabel.Text = value;
        }

        private void PasswordsRequestDialog_Load(object sender, EventArgs e)
        {
            Screen s = Screen.FromControl(this);
            Location = new Point((s.Bounds.Width - Width) / 2, (s.Bounds.Height - Height) / 2);
        }
    }
}

