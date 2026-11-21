package com.rentalsystem.android

import android.app.Activity
import android.app.AlertDialog
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.Bundle
import android.os.Environment
import android.provider.MediaStore
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.GridLayout
import android.widget.ImageButton
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.rentalsystem.android.admin.TabletAdminPasswordVerifier
import com.rentalsystem.android.claims.ClaimStatus
import com.rentalsystem.android.claims.ClaimTrackerState
import com.rentalsystem.android.mqtt.CreditMessage
import com.rentalsystem.android.mqtt.MqttConnectionStatus
import com.rentalsystem.android.mqtt.RateConfigMessage
import com.rentalsystem.android.mqtt.TabletAppConfigItemMessage
import com.rentalsystem.android.mqtt.TabletAppConfigMessage
import com.rentalsystem.android.mqtt.UpdateManifest
import com.rentalsystem.android.ota.OtaInstaller
import com.rentalsystem.android.rates.RateDisplayFormatter
import com.rentalsystem.android.sessions.SessionTrackerState
import com.rentalsystem.android.ui.RainbowFrameView
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch

// Thin view layer. All stateful objects (MQTT client, session/claim trackers,
// credit dedup) live in RentalAndroidApplication — they survive Activity
// recreates that Lock Task Mode triggers, so the broker sees a single stable
// client identifier instead of the ping-pong S25a-S25i exhibited.
class MainActivity : Activity() {
    private lateinit var kiosk: KioskController
    private lateinit var devView: LinearLayout
    private lateinit var topBar: LinearLayout
    private lateinit var customerTitle: TextView
    private lateinit var connectionBadge: View
    private lateinit var lockedStatusCluster: LinearLayout
    private lateinit var lockedConnectionBadge: View
    private lateinit var customerTimer: TextView
    private lateinit var customerStatus: TextView
    private lateinit var customerClaimProgress: TextView
    private lateinit var timeWarningBanner: TextView
    private lateinit var customerRateStatus: TextView
    private lateinit var appLauncherContainer: LinearLayout
    private lateinit var appLauncherStatus: TextView
    private lateinit var appLauncherSections: LinearLayout
    private lateinit var ownerStatus: TextView
    private lateinit var lockTaskStatus: TextView
    private lateinit var policyStatus: TextView
    private lateinit var mqttStatus: TextView
    private lateinit var sessionStatus: TextView
    private lateinit var rateStatus: TextView
    private lateinit var claimStatus: TextView
    private lateinit var creditStatus: TextView
    private lateinit var coinActionButton: Button
    private lateinit var doneButton: Button
    private lateinit var clearTimeButton: Button
    private lateinit var adminButton: Button
    private lateinit var returnToKioskButton: Button

    // Slotify logo baked onto the locked screen (on a light chip so the dark
    // logo art stays legible on the navy kiosk background). Hidden in the
    // compact active-session top bar.
    private lateinit var brandLogoChip: LinearLayout

    // Rainbow LED-frame cue overlay. Mirrors the coin box's physical RGB frame;
    // started/stopped from renderClaimState (Granted only).
    private lateinit var rainbowFrame: RainbowFrameView

    // S50b: clickable icon-only badge in the top bar. Hidden unless a newer
    // manifest is published AND it's safe to interrupt (no claim window).
    // Subtle download-arrow glyph rather than verbose text so it sits quietly
    // on the side; tap pops the confirm dialog with version + size details.
    private lateinit var updateBadge: ImageButton
    private lateinit var lockedUpdateBadge: ImageButton
    @Volatile private var updateDownloadInProgress = false

    // Activity-scoped scope for UI animations only. All MQTT publishes use
    // application scope so they survive Activity recreates mid-publish.
    private val activityScope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)

    private val app: RentalAndroidApplication by lazy {
        application as RentalAndroidApplication
    }

    private var sessionTicker: Job? = null
    private var mqttConnected = false
    private var adminUnlocked = false
    private var maintenanceMode = false
    private var devViewVisible = false
    private var latestSessionState: SessionTrackerState = SessionTrackerState.NoSession
    private var latestClaimState: ClaimTrackerState = ClaimTrackerState.Idle
    private var launcherRenderKey: String = ""

    // S27: tracks the last kiosk-state direction we applied (active vs locked)
    // so renderSessionState only fires applySessionState on actual transitions,
    // not on every 1 Hz tick. null = never applied yet (first render).
    private var lastSessionActiveApplied: Boolean? = null
    private var lastAllowedPackagesApplied: List<String> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        kiosk = KioskController(this)
        devViewVisible = savedInstanceState?.getBoolean(StateDevViewVisible, false) ?: false

        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(dp(20), dp(14), dp(20), dp(16))
            setBackgroundColor(0xFF0F172A.toInt())
        }

        customerTitle = TextView(this).apply {
            text = "Slotify"
            textSize = 17f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(0xFFF8FAFC.toInt())
            gravity = Gravity.CENTER_VERTICAL
            setOnLongClickListener {
                setDevViewVisible(!devViewVisible)
                true
            }
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
        }
        connectionBadge = View(this).apply {
            background = badgeDrawable(OfflineBadgeColor)
            layoutParams = LinearLayout.LayoutParams(dp(10), dp(10)).apply {
                leftMargin = dp(8)
                rightMargin = dp(12)
            }
        }
        lockedConnectionBadge = View(this).apply {
            background = badgeDrawable(OfflineBadgeColor)
            layoutParams = LinearLayout.LayoutParams(dp(12), dp(12)).apply {
                rightMargin = dp(8)
            }
        }

        customerTimer = TextView(this).apply {
            text = "00:00:00"
            textSize = 26f
            typeface = Typeface.create(Typeface.MONOSPACE, Typeface.BOLD)
            setTextColor(0xFFF87171.toInt())
            gravity = Gravity.CENTER_VERTICAL
            includeFontPadding = false
            setPadding(0, 0, dp(14), 0)
        }
        customerStatus = TextView(this).apply {
            text = "Insert a coin to start"
            textSize = 13f
            setTextColor(0xFFF8FAFC.toInt())
            gravity = Gravity.CENTER_VERTICAL
            maxLines = 2
            layoutParams = LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.MATCH_PARENT,
                1f
            )
        }
        customerClaimProgress = TextView(this).apply {
            text = ""
            textSize = 28f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(0xFFFDE68A.toInt())
            gravity = Gravity.CENTER
            maxLines = 4
            visibility = View.GONE
            setPadding(dp(16), dp(12), dp(16), 0)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
        }
        customerRateStatus = TextView(this).apply {
            text = "Price list loading"
            textSize = 13f
            setTextColor(0xFFCBD5E1.toInt())
            gravity = Gravity.CENTER
            setPadding(0, dp(8), 0, 0)
        }
        timeWarningBanner = TextView(this).apply {
            text = "TIME ALMOST UP"
            textSize = 15f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(0xFFFFFFFF.toInt())
            gravity = Gravity.CENTER
            visibility = View.GONE
            setPadding(dp(14), dp(9), dp(14), dp(9))
            background = roundedDrawable(0xFFD97706.toInt(), radiusDp = 12)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            ).apply {
                topMargin = dp(8)
                bottomMargin = dp(4)
            }
        }
        appLauncherStatus = TextView(this).apply {
            text = "Apps loading"
            textSize = 13f
            setTextColor(0xFFCBD5E1.toInt())
            gravity = Gravity.CENTER
            setPadding(0, dp(8), 0, dp(8))
        }
        appLauncherSections = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
        }
        val appLauncherScroll = ScrollView(this).apply {
            isFillViewport = false
            addView(
                appLauncherSections,
                ViewGroup.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.WRAP_CONTENT
                )
            )
        }
        appLauncherContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            visibility = View.GONE
            setPadding(0, dp(6), 0, 0)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                0,
                1f
            )
            addView(appLauncherStatus)
            addView(
                appLauncherScroll,
                LinearLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    0,
                    1f
                )
            )
        }

        ownerStatus = statusLine()
        lockTaskStatus = statusLine()
        policyStatus = statusLine()
        mqttStatus = statusLine().apply {
            text = "MQTT: not connected"
        }
        sessionStatus = statusLine().apply {
            text = "Session: waiting for server state"
        }
        rateStatus = statusLine().apply {
            text = "Rates: waiting for retained config"
        }
        claimStatus = statusLine().apply {
            text = "Claim: idle"
        }
        creditStatus = statusLine().apply {
            text = "Credit: waiting"
        }

        val actionRow = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
        }
        coinActionButton = Button(this).apply {
            text = "COIN"
            textSize = 12f
            isAllCaps = false
            minWidth = 0
            minHeight = 0
            setPadding(dp(10), 0, dp(10), 0)
            isEnabled = false
            setOnClickListener { requestCoinSlot() }
        }
        doneButton = Button(this).apply {
            text = "DONE"
            textSize = 12f
            isAllCaps = false
            minWidth = 0
            minHeight = 0
            setPadding(dp(10), 0, dp(10), 0)
            visibility = View.GONE
            setOnClickListener { endClaimWindow() }
        }
        clearTimeButton = Button(this).apply {
            text = "CLEAR"
            textSize = 12f
            isAllCaps = false
            minWidth = 0
            minHeight = 0
            setPadding(dp(10), 0, dp(10), 0)
            visibility = View.GONE
            setOnClickListener { clearSessionTime() }
        }
        adminButton = Button(this).apply {
            text = "Admin"
            isAllCaps = false
            visibility = if (isDebugBuild()) View.VISIBLE else View.GONE
            setOnClickListener { showAdminUnlockDialog() }
        }
        updateBadge = ImageButton(this).apply {
            setImageResource(android.R.drawable.stat_sys_download)
            setColorFilter(0xFF60A5FA.toInt())
            background = roundedDrawable(0x331976D2.toInt(), radiusDp = 14)
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            setPadding(dp(4), dp(4), dp(4), dp(4))
            contentDescription = "Software update available"
            visibility = View.GONE
            setOnClickListener { onUpdateBadgeTapped() }
            layoutParams = LinearLayout.LayoutParams(dp(28), dp(28)).apply {
                leftMargin = dp(8)
                rightMargin = dp(4)
            }
        }
        lockedUpdateBadge = ImageButton(this).apply {
            setImageResource(android.R.drawable.stat_sys_download)
            setColorFilter(0xFF60A5FA.toInt())
            background = roundedDrawable(0x331976D2.toInt(), radiusDp = 14)
            scaleType = ImageView.ScaleType.CENTER_INSIDE
            setPadding(dp(4), dp(4), dp(4), dp(4))
            contentDescription = "Software update available"
            visibility = View.GONE
            setOnClickListener { onUpdateBadgeTapped() }
            layoutParams = LinearLayout.LayoutParams(dp(28), dp(28))
        }
        lockedStatusCluster = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            visibility = View.GONE
            setPadding(dp(10), dp(8), dp(10), dp(8))
            background = roundedDrawable(0xAA111827.toInt(), radiusDp = 16)
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
                Gravity.TOP or Gravity.END
            ).apply {
                topMargin = dp(2)
                rightMargin = dp(2)
            }
            addView(lockedConnectionBadge)
            addView(lockedUpdateBadge)
        }
        returnToKioskButton = Button(this).apply {
            text = "Return to Kiosk"
            isAllCaps = false
            visibility = View.GONE
            setOnClickListener { returnToKioskFromMaintenance() }
        }
        actionRow.addView(coinActionButton)
        actionRow.addView(doneButton)
        actionRow.addView(clearTimeButton)

        val brandLogo = ImageView(this).apply {
            setImageResource(R.drawable.slotify_logo)
            adjustViewBounds = true
            scaleType = ImageView.ScaleType.FIT_CENTER
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                dp(60)
            )
        }
        brandLogoChip = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER
            visibility = View.GONE
            setPadding(dp(20), dp(12), dp(20), dp(12))
            background = roundedDrawable(0xFFF7F8FB.toInt(), radiusDp = 18)
            addView(brandLogo)
            // Admin entry point on the locked screen: the logo chip replaces the
            // long-pressable "Slotify" title here, so carry the same long-press
            // gesture (toggles the dev/admin panel). Reported 2026-06-11 — the logo
            // was blocking the admin access that previously lived on the title.
            setOnLongClickListener {
                setDevViewVisible(!devViewVisible)
                true
            }
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
        }

        topBar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(dp(12), dp(6), dp(12), dp(6))
            background = roundedDrawable(0xFF111827.toInt(), radiusDp = 12)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                dp(58)
            )
            addView(brandLogoChip)
            addView(customerTitle)
            addView(connectionBadge)
            addView(customerTimer)
            addView(customerStatus)
            addView(updateBadge)
            addView(actionRow)
        }

        val customerContent = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            layoutParams = FrameLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
        }
        customerContent.addView(topBar)
        customerContent.addView(timeWarningBanner)
        customerContent.addView(customerClaimProgress)
        customerContent.addView(appLauncherContainer)
        customerContent.addView(customerRateStatus)

        // Branding footer — always visible at the bottom.
        val brandFooter = TextView(this).apply {
            text = "by RomansTechSolutions"
            textSize = 10f
            setTextColor(0xFF374151.toInt())
            gravity = Gravity.CENTER
            setPadding(dp(0), dp(8), dp(0), dp(8))
        }
        customerContent.addView(brandFooter)

        rainbowFrame = RainbowFrameView(this)
        val customerView = FrameLayout(this).apply {
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                0,
                1f
            )
            addView(customerContent)
            addView(lockedStatusCluster)
            // Topmost, non-clickable: draws the glowing border over everything
            // while a coin window is open; touches fall through to the buttons.
            addView(
                rainbowFrame,
                FrameLayout.LayoutParams(
                    ViewGroup.LayoutParams.MATCH_PARENT,
                    ViewGroup.LayoutParams.MATCH_PARENT
                )
            )
        }

        val devTitle = TextView(this).apply {
            text = "Developer diagnostics"
            textSize = 18f
            setTextColor(0xFF38BDF8.toInt())
            gravity = Gravity.CENTER
            setPadding(0, 24, 0, 0)
        }
        val devVersion = TextView(this).apply {
            text = "Slotify v" + try { packageManager.getPackageInfo(packageName, 0).versionName ?: "?" } catch (e: Exception) { "?" }
            textSize = 13f
            setTextColor(0xFF94A3B8.toInt())
            gravity = Gravity.CENTER
            setPadding(0, 2, 0, 8)
        }
        devView = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            visibility = if (devViewVisible) View.VISIBLE else View.GONE
        }
        devView.addView(devTitle)
        devView.addView(devVersion)
        devView.addView(ownerStatus)
        devView.addView(lockTaskStatus)
        devView.addView(policyStatus)
        devView.addView(mqttStatus)
        devView.addView(sessionStatus)
        devView.addView(rateStatus)
        devView.addView(claimStatus)
        devView.addView(creditStatus)
        devView.addView(adminButton)
        devView.addView(returnToKioskButton)

        root.addView(customerView)
        root.addView(devView)
        setContentView(root)

        // Hardcoded 80% screen brightness — no user-facing controls.
        window.attributes = window.attributes.apply {
            screenBrightness = 0.8f
        }

        // Surface a config-load failure that happened in Application.onCreate —
        // MQTT didn't even start in that case.
        app.configError?.let { err ->
            mqttStatus.text = "MQTT: config error - ${err.message}"
            customerStatus.text = "Setup error. Please ask staff."
        }
    }

    override fun onSaveInstanceState(outState: Bundle) {
        outState.putBoolean(StateDevViewVisible, devViewVisible)
        super.onSaveInstanceState(outState)
    }

    override fun onStart() {
        super.onStart()
        if (app.configError != null) {
            return
        }

        // Attach UI observers to the application-scoped state holders. Each
        // observer marshals to the UI thread because the underlying callbacks
        // can fire from the Netty/MQTT thread or other background threads.
        app.sessionTracker.onStateChange = { state ->
            runOnUiThread { renderSessionState(state) }
        }
        app.claimTracker.onStateChange = { state ->
            runOnUiThread { renderClaimState(state) }
        }
        app.onConnectionStatusChanged = { status ->
            runOnUiThread { renderMqttStatus(status) }
        }
        app.onCreditApplied = { message ->
            runOnUiThread { onNewCreditApplied(message) }
        }
        app.onRateConfigChanged = { message ->
            runOnUiThread { applyRateConfig(message) }
        }
        app.onTabletAppConfigChanged = { message ->
            runOnUiThread { applyTabletAppConfig(message) }
        }
        app.onAdminOpenRequested = {
            runOnUiThread {
                if (!maintenanceMode) {
                    enterMaintenanceMode()
                }
            }
        }
        app.onUpdateManifestChanged = { manifest ->
            runOnUiThread { applyUpdateManifest(manifest) }
        }
        app.kickReconnect()

        // Render whatever state Application already holds — keeps the UI in
        // sync with messages that arrived before this Activity instance attached.
        app.lastConnectionStatus?.let { renderMqttStatus(it) }
        renderSessionState(app.sessionTracker.getCurrentState())
        renderClaimState(app.claimTracker.getCurrentState())
        app.rateConfig?.let { applyRateConfig(it) }
        app.tabletAppConfig?.let { applyTabletAppConfig(it) }
        app.updateManifest?.let { applyUpdateManifest(it) }

        startSessionTicker()
    }

    override fun onResume() {
        super.onResume()
        window.attributes = window.attributes.apply { screenBrightness = 0.8f }
        if (adminUnlocked) {
            renderKioskState(kiosk.readState(listOf("Admin unlock: kiosk disabled until app restart.")))
            return
        }
        if (maintenanceMode) {
            renderKioskState(kiosk.readState(listOf("Admin maintenance: reopen Rental to return to kiosk.")))
            return
        }
        // S27: drive the kiosk state from the session state instead of blindly
        // calling configureAndStart on every onResume. configureAndStart
        // unconditionally re-engaged Lock Task and re-installed the rental
        // app as Home, so pressing Home during an active session looped
        // straight back to the kiosk — defeating the active-session release.
        renderKioskState(kiosk.applySessionState(isSessionActiveNow(), enabledInstalledTabletAppPackages()))
    }

    private fun isSessionActiveNow(): Boolean {
        if (app.configError != null) return false
        val state = app.sessionTracker.getCurrentState()
        return state.hasSession && !state.locked && state.effectiveRemainingSec > 0
    }

    override fun onStop() {
        super.onStop()
        sessionTicker?.cancel()
        sessionTicker = null
        // Don't leave the LED-frame animator running while backgrounded;
        // onStart -> renderClaimState restarts it if a window is still open.
        if (::rainbowFrame.isInitialized) {
            rainbowFrame.stop()
        }

        // Detach UI observers from Application so it does not call into a
        // backgrounded Activity. Application keeps running with no observers
        // — MQTT stays connected, trackers retain monotonic state.
        if (app.configError == null) {
            app.sessionTracker.onStateChange = null
            app.claimTracker.onStateChange = null
            app.onConnectionStatusChanged = null
            app.onCreditApplied = null
            app.onRateConfigChanged = null
            app.onTabletAppConfigChanged = null
            app.onAdminOpenRequested = null
            app.onUpdateManifestChanged = null
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        activityScope.cancel()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        // Only re-hide the system bars when the kiosk is actually active
        // (Device Owner + Lock Task engaged). After Admin unlock the bars
        // must stay visible so the user can press Home and leave the
        // rental app — otherwise the tablet looks "still locked" even
        // though policies are cleared. Reported 2026-05-23 by the user
        // ("tablet still locked Cant be used" after Admin unlock).
        if (hasFocus && !adminUnlocked && !maintenanceMode && kiosk.isKioskActiveNow()) {
            kiosk.hideSystemBars()
        }
    }

    private fun refreshKioskState() {
        val state = kiosk.configureAndStart()
        renderKioskState(state)
    }

    private fun renderKioskState(state: KioskState) {
        ownerStatus.text = when {
            state.isDeviceOwner -> "Device Owner: active"
            state.isProfileOwner -> "Profile Owner: active"
            state.isAdminActive -> "Device Admin: active"
            else -> "Device Owner: not set"
        }
        lockTaskStatus.text = if (state.isLockTaskActive) {
            "Lock Task: active"
        } else if (state.isLockTaskPermitted) {
            "Lock Task: permitted"
        } else {
            "Lock Task: not permitted"
        }
        policyStatus.text = if (state.policyNotes.isEmpty()) {
            "Policies: ready"
        } else {
            state.policyNotes.last()
        }
    }

    private fun showAdminUnlockDialog() {
        val passwordInput = EditText(this).apply {
            // Plain visible password input — kiosk-only screen, no shoulder-surfing
            // concern, and customers on Xiaomi/HyperOS soft keyboards have trouble
            // Reported 2026-05-23: "admin access is also not working on tablet now".
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS
            hint = "Password"
            setSingleLine(true)
        }

        AlertDialog.Builder(this)
            .setTitle("Admin access")
            .setMessage("Enter the admin password to open maintenance mode. Device Owner stays active so the tablet can return to kiosk from this app.")
            .setView(passwordInput)
            .setNegativeButton("Cancel", null)
            .setPositiveButton("Maintenance") { _, _ ->
                // Trim defensively: many soft keyboards auto-insert a trailing
                // space when the user taps the Unlock button after a special
                // character, and leading whitespace from accidental swipes is
                // also common on shared tablets.
                val entered = passwordInput.text.toString().trim()
                if (isAdminPasswordValid(entered)) {
                    enterMaintenanceMode()
                } else {
                    policyStatus.text = "Admin access: wrong password"
                }
            }
            .show()
    }

    private fun isAdminPasswordValid(entered: String): Boolean {
        return TabletAdminPasswordVerifier.isValid(
            entered,
            app.tabletAdminConfig,
            LegacyAdminPassword
        )
    }

    private fun enterMaintenanceMode() {
        maintenanceMode = true
        devViewVisible = true
        setDevViewVisible(true)
        adminButton.visibility = View.GONE
        returnToKioskButton.visibility = View.VISIBLE
        renderKioskState(kiosk.enterAdminMaintenanceMode())
    }

    private fun returnToKioskFromMaintenance() {
        maintenanceMode = false
        adminUnlocked = false
        lastSessionActiveApplied = null
        lastAllowedPackagesApplied = emptyList()
        returnToKioskButton.visibility = View.GONE
        adminButton.visibility = if (isDebugBuild()) View.VISIBLE else View.GONE
        val active = isSessionActiveNow()
        renderKioskState(kiosk.applySessionState(active, if (active) enabledInstalledTabletAppPackages() else emptyList()))
        renderSessionState(app.sessionTracker.getCurrentState())
    }

    private fun isDebugBuild(): Boolean =
        (applicationInfo.flags and ApplicationInfo.FLAG_DEBUGGABLE) != 0

    private fun renderMqttStatus(status: MqttConnectionStatus) {
        mqttConnected = status.connected
        connectionBadge.background = badgeDrawable(if (status.connected) OnlineBadgeColor else OfflineBadgeColor)
        lockedConnectionBadge.background = badgeDrawable(if (status.connected) OnlineBadgeColor else OfflineBadgeColor)
        val prefix = if (status.connected) "MQTT: ONLINE" else "MQTT: offline"
        val detail = status.detail?.takeIf { it.isNotBlank() }?.let { " ($it)" } ?: ""
        mqttStatus.text = "$prefix - ${status.message}$detail"
        renderCustomerState()
        renderClaimState(app.claimTracker.getCurrentState())
    }

    private fun startSessionTicker() {
        if (sessionTicker?.isActive == true) {
            return
        }

        sessionTicker = activityScope.launch {
            while (isActive) {
                renderSessionState(app.sessionTracker.getCurrentState())
                val claimState = app.claimTracker.getCurrentState()
                if (claimState.status == ClaimStatus.Granted && claimState.grantedRemainingSec <= 0) {
                    app.claimTracker.markTimedOut()
                } else if (claimState.status == ClaimStatus.Denied &&
                    claimState.denyReason == "abuse_ban" &&
                    claimState.banUntil != null &&
                    isBanExpired(claimState.banUntil)) {
                    app.claimTracker.reset()
                } else {
                    renderClaimState(claimState)
                }
                delay(1000)
            }
        }
    }

    private fun renderSessionState(state: SessionTrackerState) {
        latestSessionState = state
        // S27: drive the kiosk policy state from the session state so the
        // customer can actually use the tablet while their session is active.
        // Guarded by lastSessionActiveApplied so the 1 Hz ticker does not
        // re-issue DPM calls every second — only on actual transitions.
        maybeApplyKioskStateForSession(state)

        if (!state.hasSession) {
            sessionStatus.text = "Session: waiting for server state"
            sessionStatus.setTextColor(0xFFCBD5E1.toInt())
            renderRateState(state)
            renderCustomerState()
            renderClaimState(app.claimTracker.getCurrentState())
            return
        }

        if (state.locked || state.effectiveRemainingSec <= 0) {
            sessionStatus.text = "Session: LOCKED (${state.stationId})"
            sessionStatus.setTextColor(0xFFF87171.toInt())
            renderRateState(state)
            renderCustomerState()
            renderClaimState(app.claimTracker.getCurrentState())
            return
        }

        sessionStatus.text = "Session: ACTIVE ${state.formatRemaining()} (${state.stationId})"
        sessionStatus.setTextColor(0xFF86EFAC.toInt())
        renderRateState(state)
        renderCustomerState()
        renderClaimState(app.claimTracker.getCurrentState())
    }

    private fun maybeApplyKioskStateForSession(state: SessionTrackerState) {
        if (adminUnlocked || maintenanceMode) {
            return
        }
        if (!state.hasSession) {
            return
        }
        val active = state.hasSession && !state.locked && state.effectiveRemainingSec > 0
        val allowedPackages = if (active) enabledInstalledTabletAppPackages() else emptyList()
        if (lastSessionActiveApplied == active && (!active || lastAllowedPackagesApplied == allowedPackages)) {
            return
        }
        val wasActive = lastSessionActiveApplied
        lastSessionActiveApplied = active
        lastAllowedPackagesApplied = allowedPackages
        renderKioskState(kiosk.applySessionState(active, allowedPackages))

        // S28: on the locked→active edge, send the rental Activity to the
        // background automatically. Customer just paid (or had time added);
        // they should land on the system launcher without having to press
        // Home themselves. The persistent notification keeps the countdown +
        // Add Time / Clear Time visible in the shade.
        //
        // Guarded by claim-in-flight: during an open coin-insertion window
        // the customer must see "Drop Coins (Ns)" and the Done button. If
        // the session goes active mid-window (e.g. a locked Insert Coin
        // reached the unlock threshold), the minimize is held back until
        // the claim resolves — at Done / TTL the claim transitions to a
        // non-in-flight state, which fires renderClaimState's late-minimize
        // hook below. Reported 2026-05-24: "dont auto minimize if done
        // button is not yet clicked or the insert coin countdown not yet
        // finished".
        if (active && wasActive == false) {
            launcherRenderKey = ""
            renderLauncherState(hasActiveTime = true)
        }
    }

    /*
     * current Task — we don't yank a customer back from another app.
    */
    private fun applyRateConfig(message: RateConfigMessage) {
        renderRateState(app.sessionTracker.getCurrentState())
    }

    private fun applyTabletAppConfig(message: TabletAppConfigMessage) {
        launcherRenderKey = ""
        if (isSessionActiveNow()) {
            renderKioskState(kiosk.applySessionState(true, enabledInstalledTabletAppPackages()))
            lastAllowedPackagesApplied = enabledInstalledTabletAppPackages()
        }
        renderLauncherState(isSessionActiveNow())
    }

    // S50b: fresh retained `rental/config/update` arrived. Just refresh the
    // badge visibility — version comparison and gating happens there so this
    // method stays a single entry point that other state changes can call.
    private fun applyUpdateManifest(message: UpdateManifest) {
        refreshUpdateBadge()
    }

    // Reads the installed APK's gradle `versionName` via PackageManager. This
    // is the source of truth for what's actually installed on the tablet —
    // appsettings.json is a per-machine config that the OTA install does NOT
    // update, so reading it would make the badge keep reappearing after a
    // successful update (same bug we fixed on Windows for FileVersion).
    private fun installedVersionName(): String? {
        val info = try {
            packageManager.getPackageInfo(packageName, 0)
        } catch (_: PackageManager.NameNotFoundException) {
            return null
        }
        return info.versionName?.takeIf { it.isNotBlank() }
    }

    // Compare two semver-shaped strings as integer-component versions. Returns
    // null if either side is unparseable. Handles 3-part vs 4-part: missing
    // components are treated as 0 so "1.0.2" == "1.0.2.0". Component-wise
    // comparison is integer (so "1.0.10" > "1.0.2", unlike string compare).
    private fun compareSemver(left: String, right: String): Int? {
        val l = left.split('.').mapNotNull { it.toIntOrNull() }
        val r = right.split('.').mapNotNull { it.toIntOrNull() }
        if (l.isEmpty() || r.isEmpty()) return null
        val len = maxOf(l.size, r.size)
        for (i in 0 until len) {
            val a = l.getOrElse(i) { 0 }
            val b = r.getOrElse(i) { 0 }
            if (a != b) return a.compareTo(b)
        }
        return 0
    }

    // Decide whether the Update badge should appear. Hides during claim
    // windows (Pending/Granted) and during a download. Visible when the
    // server announced a strictly-newer android.version than ours.
    private fun refreshUpdateBadge() {
        updateBadge.visibility = View.GONE
        lockedUpdateBadge.visibility = View.GONE

        val pending = app.updateManifest?.android
        if (pending == null || pending.version.isBlank() || pending.url.isBlank()) {
            return
        }

        val current = installedVersionName()
        val cmp = current?.let { compareSemver(pending.version, it) }
        // Only suppress when we can prove pending is NOT newer; if we cannot
        // determine the running version, leave the badge visible so the
        // operator can still install manually.
        if (cmp != null && cmp <= 0) {
            return
        }

        if (updateDownloadInProgress) {
            return
        }

        // Never interrupt a payment in progress.
        val claimStatus = latestClaimState.status
        if (claimStatus == ClaimStatus.Pending || claimStatus == ClaimStatus.Granted) {
            return
        }

        // Icon-only badge; the confirm dialog on tap shows version + size details.
        val description = "Update v${pending.version} available"
        updateBadge.contentDescription = description
        lockedUpdateBadge.contentDescription = description
        if (hasActiveCustomerTime()) {
            updateBadge.visibility = View.VISIBLE
        } else {
            lockedUpdateBadge.visibility = View.VISIBLE
        }
    }

    private fun onUpdateBadgeTapped() {
        val pending = app.updateManifest?.android ?: return
        if (updateDownloadInProgress) return

        val current = installedVersionName() ?: "unknown"
        val sizeMb = pending.sizeBytes / 1024 / 1024
        val deviceOwner = kiosk.readState().isDeviceOwner
        val installNote = if (deviceOwner) {
            "The update installs automatically and the app restarts."
        } else {
            "After download, Android will ask you to install. " +
                "Tap Install in the system dialog."
        }
        AlertDialog.Builder(this)
            .setTitle("Update available")
            .setMessage(
                "Update v${pending.version} ready to download.\n" +
                    "Current version: $current\n" +
                    "Download size: ${sizeMb} MB\n\n" +
                    installNote
            )
            .setPositiveButton("Download") { _, _ -> downloadAndInstallUpdate(pending) }
            .setNegativeButton("Later", null)
            .show()
    }

    private fun downloadAndInstallUpdate(component: com.rentalsystem.android.mqtt.UpdateComponent) {
        updateDownloadInProgress = true
        refreshUpdateBadge()
        Toast.makeText(this, "Downloading update\u2026", Toast.LENGTH_SHORT).show()

        // Device Owner installs silently via PackageInstaller; everyone else
        // falls back to the manual File Manager sideload. Read the policy state
        // on the UI thread before dropping to the IO coroutine.
        val deviceOwner = kiosk.readState().isDeviceOwner

        activityScope.launch {
            val result = try {
                kotlinx.coroutines.withContext(Dispatchers.IO) {
                    val otaDir = java.io.File(getExternalFilesDir(null), "ota")
                    if (!otaDir.exists()) otaDir.mkdirs()
                    val tempFile = java.io.File(otaDir, "RentalAndroid.apk")

                    val url = java.net.URL(component.url)
                    val conn = url.openConnection() as java.net.HttpURLConnection
                    conn.connectTimeout = 15000
                    conn.readTimeout = 60000
                    conn.requestMethod = "GET"
                    conn.inputStream.use { input ->
                        tempFile.outputStream().use { output -> input.copyTo(output) }
                    }
                    conn.disconnect()

                    val digest = java.security.MessageDigest.getInstance("SHA-256")
                    tempFile.inputStream().use { stream ->
                        val buf = ByteArray(8192)
                        while (true) {
                            val n = stream.read(buf)
                            if (n <= 0) break
                            digest.update(buf, 0, n)
                        }
                    }
                    val actualHash = digest.digest().joinToString("") { "%02x".format(it) }
                    if (!actualHash.equals(component.sha256, ignoreCase = true)) {
                        tempFile.delete()
                        return@withContext UpdateDownloadResult.Failure(
                            "SHA-256 mismatch \u2014 refusing to install.\n" +
                                "Expected: ${component.sha256.take(16)}\u2026\n" +
                                "Got:      ${actualHash.take(16)}\u2026"
                        )
                    }

                    if (deviceOwner) {
                        // Keep the verified APK on internal storage; the
                        // PackageInstaller session reads it directly. No
                        // MediaStore / File Manager round-trip needed.
                        UpdateDownloadResult.Success(component.version, tempFile)
                    } else {
                        // Non-Device-Owner fallback: stage to public Downloads
                        // via MediaStore so File Manager can browse to it.
                        stageToDownloads(tempFile, component.version)
                        tempFile.delete()
                        UpdateDownloadResult.Success(component.version, null)
                    }
                }
            } catch (t: Throwable) {
                UpdateDownloadResult.Failure(t.message ?: t.javaClass.simpleName)
            } finally {
                updateDownloadInProgress = false
            }

            when (result) {
                is UpdateDownloadResult.Success -> {
                    val apk = result.apk
                    if (apk != null) {
                        installViaDeviceOwner(result.version, apk)
                    } else {
                        showManualSideloadDialog(result.version)
                    }
                }
                is UpdateDownloadResult.Failure -> {
                    AlertDialog.Builder(this@MainActivity)
                        .setTitle("Update failed")
                        .setMessage(result.reason)
                        .setPositiveButton("OK", null)
                        .show()
                }
            }
            refreshUpdateBadge()
        }
    }

    /**
     * Copies the verified APK into public Downloads via MediaStore. Used only
     * on the non-Device-Owner fallback path. Runs on the IO dispatcher.
     */
    private fun stageToDownloads(apk: java.io.File, version: String) {
        val values = android.content.ContentValues().apply {
            put(MediaStore.Downloads.DISPLAY_NAME, "RentalAndroid-${version}.apk")
            put(MediaStore.Downloads.MIME_TYPE, "application/vnd.android.package-archive")
        }
        val uri = contentResolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
            ?: throw java.io.IOException("Failed to create MediaStore entry")
        contentResolver.openOutputStream(uri)?.use { output ->
            apk.inputStream().use { input -> input.copyTo(output) }
        } ?: throw java.io.IOException("Failed to open output stream")
    }

    /**
     * Device Owner silent install via [OtaInstaller]. On a self-update the
     * process is usually killed the instant the swap lands, so STATUS_SUCCESS
     * rarely arrives \u2014 the relaunch picks up the new version. Only failure and
     * the (unexpected for a DO) pending-user-action path are handled here.
     */
    private fun installViaDeviceOwner(version: String, apk: java.io.File) {
        Toast.makeText(this, "Installing update\u2026", Toast.LENGTH_SHORT).show()
        try {
            OtaInstaller(this).install(apk) { status, message, userActionIntent ->
                runOnUiThread {
                    when (status) {
                        PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                            userActionIntent?.let {
                                it.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                                try {
                                    startActivity(it)
                                } catch (t: Throwable) {
                                    apk.delete()
                                    showInstallFailedDialog(
                                        version, t.message ?: "could not open installer")
                                }
                            }
                        }
                        PackageInstaller.STATUS_SUCCESS -> {
                            apk.delete()
                            Toast.makeText(
                                this, "Update installed", Toast.LENGTH_LONG).show()
                        }
                        else -> {
                            apk.delete()
                            showInstallFailedDialog(version, message ?: "status $status")
                        }
                    }
                }
            }
        } catch (t: Throwable) {
            apk.delete()
            showInstallFailedDialog(version, t.message ?: t.javaClass.simpleName)
        }
    }

    /**
     * Shown when the silent Device Owner install could not complete. Offers the
     * manual File Manager procedure as a fallback by re-staging to Downloads.
     */
    private fun showInstallFailedDialog(version: String, reason: String) {
        AlertDialog.Builder(this)
            .setTitle("Auto-install failed")
            .setMessage(
                "Could not install v$version automatically:\n$reason\n\n" +
                    "Tap \"Manual install\" to save the APK to Downloads and " +
                    "follow the File Manager procedure instead."
            )
            .setPositiveButton("Manual install") { _, _ ->
                updateDownloadInProgress = true
                refreshUpdateBadge()
                activityScope.launch {
                    val staged = try {
                        kotlinx.coroutines.withContext(Dispatchers.IO) {
                            val tempFile = java.io.File(
                                java.io.File(getExternalFilesDir(null), "ota"),
                                "RentalAndroid.apk"
                            )
                            // Re-download if the verified copy was already deleted.
                            val pending = app.updateManifest?.android
                            if (!tempFile.exists() && pending != null) {
                                java.net.URL(pending.url).openStream().use { input ->
                                    tempFile.outputStream().use { out -> input.copyTo(out) }
                                }
                            }
                            stageToDownloads(tempFile, version)
                            tempFile.delete()
                            true
                        }
                    } catch (_: Throwable) {
                        false
                    } finally {
                        updateDownloadInProgress = false
                    }
                    if (staged) {
                        showManualSideloadDialog(version)
                    } else {
                        Toast.makeText(
                            this@MainActivity,
                            "Could not stage APK to Downloads",
                            Toast.LENGTH_LONG
                        ).show()
                    }
                    refreshUpdateBadge()
                }
            }
            .setNegativeButton("Cancel", null)
            .show()
    }

    private fun showManualSideloadDialog(version: String) {
        AlertDialog.Builder(this)
            .setTitle("Update downloaded")
            .setMessage(
                "RentalAndroid-${version}.apk saved to Downloads.\n\n" +
                    "HyperOS blocks sideload installs while Device Owner is " +
                    "active. To install:\n\n" +
                    "1. Open admin panel \u2192 clear Device Owner\n" +
                    "2. Open File Manager \u2192 Downloads\n" +
                    "3. Tap the APK to install\n" +
                    "4. Re-provision Device Owner after install\n\n" +
                    "(Avoid the Recent view \u2014 browse to Downloads directly.)"
            )
            .setPositiveButton("Open Downloads") { _, _ ->
                try {
                    val intent = Intent(Intent.ACTION_VIEW).apply {
                        setDataAndType(
                            MediaStore.Downloads.EXTERNAL_CONTENT_URI,
                            "application/vnd.android.package-archive"
                        )
                        addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    }
                    startActivity(intent)
                } catch (_: Throwable) {
                    Toast.makeText(
                        this, "Open File Manager \u2192 Downloads manually",
                        Toast.LENGTH_LONG).show()
                }
            }
            .setNegativeButton("Later", null)
            .show()
    }

    private sealed class UpdateDownloadResult {
        data class Success(val version: String, val apk: java.io.File?) : UpdateDownloadResult()
        data class Failure(val reason: String) : UpdateDownloadResult()
    }

    private fun renderRateState(state: SessionTrackerState) {
        val config = app.rateConfig
        if (config == null) {
            rateStatus.text = "Rates: waiting for retained config"
            rateStatus.setTextColor(0xFFCBD5E1.toInt())
            customerRateStatus.text = "Price list loading"
            return
        }

        rateStatus.text = RateDisplayFormatter.buildLockedRateText(
            sessionDeviceType = state.deviceType,
            fallbackDeviceType = app.clientConfig.deviceType,
            rateConfig = config
        )
        rateStatus.setTextColor(0xFFFACC15.toInt())
        customerRateStatus.text = RateDisplayFormatter.buildCustomerRateText(
            sessionDeviceType = state.deviceType,
            fallbackDeviceType = app.clientConfig.deviceType,
            rateConfig = config,
            hasActiveSession = state.hasSession && !state.locked && state.effectiveRemainingSec > 0
        )
    }

    private fun renderClaimState(state: ClaimTrackerState) {
        latestClaimState = state
        // Rainbow LED frame mirrors the coin box's physical RGB frame: glow only
        // while the coin window is open. Driven off claim state, not a timer.
        if (::rainbowFrame.isInitialized) {
            if (state.status == ClaimStatus.Granted) rainbowFrame.start() else rainbowFrame.stop()
        }
        val sessionState = app.sessionTracker.getCurrentState()
        val hasActiveTime =
            sessionState.hasSession &&
                !sessionState.locked &&
                sessionState.effectiveRemainingSec > 0
        val claimInFlight = state.status == ClaimStatus.Pending || state.status == ClaimStatus.Granted

        // S28 late-minimize: if a previous session transition wanted to
        // minimize but a claim was still in flight, drain the pending
        // request now that the claim has resolved. Only minimize when the
        // session is still active — a Done with no coins on a locked
        // Insert Coin leaves the session locked, so the kiosk stays
        // engaged and we should NOT send the activity to the back.
            // Claim resolved but session went back to locked — drop the
            // pending request so a later transition starts clean.
        val canStartClaim =
            mqttConnected &&
                (state.status == ClaimStatus.Idle ||
                    state.status == ClaimStatus.Denied ||
                    state.status == ClaimStatus.TimedOut ||
                    state.status == ClaimStatus.OfflineTimeout) &&
                state.denyReason != "station_unknown" &&
                state.denyReason != "abuse_ban"

        claimStatus.text = when (state.status) {
            ClaimStatus.Idle -> "Claim: idle"
            ClaimStatus.Pending -> "Claim: requesting coin slot"
            ClaimStatus.Granted -> "Claim: drop coins - ${state.grantedRemainingSec}s"
            ClaimStatus.Denied -> if (state.heldBy != null) {
                "Claim: denied (${state.denyReason}, held by ${state.heldBy})"
            } else {
                "Claim: denied (${state.denyReason})"
            }
            ClaimStatus.TimedOut -> "Claim: timed out"
            ClaimStatus.OfflineTimeout -> "Claim: coin slot offline"
        }
        renderCustomerState()

        // S28g: the "Coin slot offline — try again." Toast moved to
        // RentalAndroidApplication.startPendingWatchdog so the customer
        // gets feedback even when the rental Activity is backgrounded
        // (i.e. after S28a auto-minimize sent us to the system launcher).
        // Toasting from Activity here would double-toast when foreground.

        val baseLabel = if (hasActiveTime) "+TIME" else "INSERT COIN"
        coinActionButton.text = when (state.status) {
            ClaimStatus.Pending -> if (hasActiveTime) "..." else "REQUESTING..."
            ClaimStatus.Granted -> if (hasActiveTime) {
                "COINS ${state.grantedRemainingSec}s"
            } else {
                "DROP COINS ${state.grantedRemainingSec}s"
            }
            else -> if (mqttConnected) baseLabel else if (hasActiveTime) "OFF" else "OFFLINE"
        }
        coinActionButton.isEnabled = canStartClaim && !claimInFlight

        doneButton.visibility = if (claimInFlight) View.VISIBLE else View.GONE
        // Disable DONE during locked Insert Coin claims if the starter minimum
        // hasn't been met yet — prevents kids from accidentally forfeiting coins.
        val belowStarterMinimum = !hasActiveTime &&
            state.runningTotalPesos > 0 &&
            state.runningTotalPesos < getStarterMinimum()
        doneButton.isEnabled = state.canShowDoneButton && mqttConnected && !belowStarterMinimum

        clearTimeButton.visibility = if (hasActiveTime && !claimInFlight) View.VISIBLE else View.GONE
        clearTimeButton.isEnabled = mqttConnected && hasActiveTime && !claimInFlight

        refreshUpdateBadge()
    }

    private fun renderCustomerState() {
        val session = latestSessionState
        val claim = latestClaimState
        val hasActiveTime = hasActiveCustomerTime()

        applyCustomerLayoutMode(hasActiveTime)
        // During a locked Insert Coin window the session is not credited until
        // DONE, so the big timer would otherwise sit at 00:00:00. Mirror Add Time:
        // drive it from the claim's running accumulation so the customer watches
        // the banked time grow per coin (00:00:00 below the starter minimum, then
        // it jumps once the minimum is reached).
        val lockedAccumulating = !hasActiveTime && claim.status == ClaimStatus.Granted
        customerTimer.text = when {
            hasActiveTime -> session.formatRemaining()
            lockedAccumulating -> formatDuration(claim.runningTotalSec)
            else -> "00:00:00"
        }
        customerTimer.setTextColor(
            when {
                hasActiveTime -> 0xFF86EFAC.toInt()
                lockedAccumulating -> 0xFFFDE68A.toInt()
                else -> 0xFFF87171.toInt()
            }
        )
        customerStatus.text = buildCustomerStatus(session, claim, hasActiveTime)
        customerStatus.setTextColor(
            when (claim.status) {
                ClaimStatus.Denied,
                ClaimStatus.OfflineTimeout -> 0xFFFCA5A5.toInt()
                ClaimStatus.Pending,
                ClaimStatus.Granted -> 0xFFFDE68A.toInt()
                else -> 0xFFF8FAFC.toInt()
            }
        )
        renderTimeEndingWarning(session, hasActiveTime)
        renderLockedClaimProgress(session, claim, hasActiveTime)
        renderLauncherState(hasActiveTime)
    }

    private fun renderTimeEndingWarning(session: SessionTrackerState, hasActiveTime: Boolean) {
        if (!::timeWarningBanner.isInitialized) {
            return
        }

        val warning = app.clientConfig.countdownWarning
        val remaining = session.effectiveRemainingSec
        if (!warning.enabled ||
            !hasActiveTime ||
            remaining <= 0 ||
            remaining > warning.firstWarningSec
        ) {
            timeWarningBanner.visibility = View.GONE
            return
        }

        if (remaining <= warning.finalCountdownStartSec) {
            timeWarningBanner.text = "TIME ENDS IN ${remaining}s"
            timeWarningBanner.background = roundedDrawable(0xFFDC2626.toInt(), radiusDp = 12)
        } else {
            timeWarningBanner.text = "TIME ALMOST UP - ${session.formatRemaining()} LEFT"
            timeWarningBanner.background = roundedDrawable(0xFFD97706.toInt(), radiusDp = 12)
        }
        timeWarningBanner.visibility = View.VISIBLE
    }

    private fun renderLockedClaimProgress(
        session: SessionTrackerState,
        claim: ClaimTrackerState,
        hasActiveTime: Boolean
    ) {
        // Show the running coin accumulation for the whole locked Insert Coin
        // window (from the moment the slot is granted, even at the zero-peso
        // start), so the customer always sees how much they have put in - the
        // peso half of the Add-Time-style live tracker (the time half is the big
        // timer above).
        if (hasActiveTime || claim.status != ClaimStatus.Granted) {
            customerClaimProgress.visibility = View.GONE
            customerClaimProgress.text = ""
            return
        }

        val rule = RateDisplayFormatter.getInsertCoinRule(
            sessionDeviceType = session.deviceType,
            fallbackDeviceType = app.clientConfig.deviceType,
            rateConfig = app.rateConfig
        )
        val pesos = claim.runningTotalPesos
        val minPesos = rule.starterMaxPesos
        val hint = when {
            pesos <= 0 -> "Insert ${formatPesos(minPesos)} or more to unlock"
            pesos < minPesos -> "${formatPesos(minPesos - pesos)} more to unlock"
            else -> "Ready to start"
        }
        customerClaimProgress.text = "${formatPesos(pesos)}\n$hint"
        customerClaimProgress.setTextColor(
            if (pesos < minPesos) 0xFFFCA5A5.toInt() else 0xFF86EFAC.toInt()
        )
        customerClaimProgress.visibility = View.VISIBLE
    }

    private fun applyCustomerLayoutMode(hasActiveTime: Boolean) {
        if (hasActiveTime) {
            topBar.orientation = LinearLayout.HORIZONTAL
            topBar.gravity = Gravity.CENTER_VERTICAL
            topBar.setPadding(dp(12), dp(6), dp(12), dp(6))
            topBar.background = roundedDrawable(0xFF111827.toInt(), radiusDp = 12)
            lockedStatusCluster.visibility = View.GONE
            lockedUpdateBadge.visibility = View.GONE
            connectionBadge.visibility = View.VISIBLE
            brandLogoChip.visibility = View.GONE
            customerTitle.visibility = View.VISIBLE
            topBar.layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                dp(58)
            )
            customerTitle.textSize = 17f
            customerTitle.gravity = Gravity.CENTER_VERTICAL
            customerTitle.layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.MATCH_PARENT
            )
            connectionBadge.layoutParams = LinearLayout.LayoutParams(dp(10), dp(10)).apply {
                leftMargin = dp(8)
                rightMargin = dp(12)
            }
            customerTimer.textSize = 26f
            customerTimer.gravity = Gravity.CENTER_VERTICAL
            customerTimer.setPadding(0, 0, dp(14), 0)
            customerStatus.textSize = 13f
            customerStatus.gravity = Gravity.CENTER_VERTICAL
            customerStatus.maxLines = 2
            customerStatus.layoutParams = LinearLayout.LayoutParams(
                0,
                ViewGroup.LayoutParams.MATCH_PARENT,
                1f
            )
            setCoinButtonCompact()
            return
        }

        topBar.orientation = LinearLayout.VERTICAL
        topBar.gravity = Gravity.CENTER
        topBar.setPadding(dp(24), dp(24), dp(24), dp(24))
        topBar.background = null
        lockedStatusCluster.visibility = View.VISIBLE
        brandLogoChip.visibility = View.VISIBLE
        customerTitle.visibility = View.GONE
            connectionBadge.visibility = View.GONE
            updateBadge.visibility = View.GONE
            topBar.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            0,
            1f
        )
        customerTitle.textSize = 32f
        customerTitle.gravity = Gravity.CENTER
        customerTitle.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        )
        connectionBadge.layoutParams = LinearLayout.LayoutParams(dp(12), dp(12)).apply {
            topMargin = dp(10)
        }
        customerTimer.textSize = 72f
        customerTimer.gravity = Gravity.CENTER
        customerTimer.setPadding(0, dp(24), 0, 0)
        customerStatus.textSize = 22f
        customerStatus.gravity = Gravity.CENTER
        customerStatus.maxLines = 3
        customerStatus.layoutParams = LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT
        ).apply {
            topMargin = dp(18)
        }
        setCoinButtonLarge()
    }

    private fun hasActiveCustomerTime(): Boolean {
        val session = latestSessionState
        return session.hasSession && !session.locked && session.effectiveRemainingSec > 0
    }

    private fun setCoinButtonCompact() {
        listOf(coinActionButton, doneButton, clearTimeButton).forEach { button ->
            button.textSize = 12f
            button.minWidth = 0
            button.minHeight = 0
            button.setPadding(dp(10), 0, dp(10), 0)
            button.layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                dp(42)
            )
        }
    }

    private fun setCoinButtonLarge() {
        listOf(coinActionButton, doneButton, clearTimeButton).forEach { button ->
            button.textSize = 22f
            button.minWidth = dp(180)
            button.minHeight = dp(56)
            button.setPadding(dp(28), 0, dp(28), 0)
            button.layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                dp(60)
            ).apply {
                topMargin = dp(22)
            }
        }
    }

    private fun renderLauncherState(hasActiveTime: Boolean) {
        if (!::appLauncherContainer.isInitialized) {
            return
        }

        if (!hasActiveTime) {
            appLauncherContainer.visibility = View.GONE
            launcherRenderKey = ""
            return
        }

        appLauncherContainer.visibility = View.VISIBLE
        val config = app.tabletAppConfig
        if (config == null) {
            appLauncherStatus.text = "Apps loading from server"
            appLauncherSections.removeAllViews()
            launcherRenderKey = "loading"
            return
        }

        val installedApps = config.apps
            .asSequence()
            .filter { it.enabled }
            .sortedWith(compareBy<TabletAppConfigItemMessage> { it.sortOrder }.thenBy { it.displayName })
            .filter { isPackageInstalled(it.packageName) }
            .toList()

        val key = "${config.version}|" + installedApps.joinToString("|") { it.packageName }
        if (launcherRenderKey == key) {
            return
        }
        launcherRenderKey = key

        appLauncherSections.removeAllViews()
        if (installedApps.isEmpty()) {
            appLauncherStatus.text = "No allowed apps installed. Ask staff."
            return
        }

        appLauncherStatus.text = "Choose an app"
        installedApps
            .groupBy { classifyLauncherApp(it) }
            .toSortedMap(compareBy { it.sortOrder })
            .forEach { (category, items) ->
                appLauncherSections.addView(buildLauncherSection(category, items))
            }
    }

    private fun buildLauncherSection(
        category: LauncherCategory,
        items: List<TabletAppConfigItemMessage>
    ): View {
        val section = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER_HORIZONTAL
            setPadding(0, dp(2), 0, dp(10))
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
        }

        val title = TextView(this).apply {
            text = category.title
            textSize = 14f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(0xFF93C5FD.toInt())
            gravity = Gravity.CENTER
            includeFontPadding = false
            setPadding(0, 0, 0, dp(6))
        }

        val grid = GridLayout(this).apply {
            columnCount = launcherColumnCount()
            alignmentMode = GridLayout.ALIGN_BOUNDS
            useDefaultMargins = false
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.WRAP_CONTENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
        }
        items.forEach { item ->
            grid.addView(buildLauncherTile(item))
        }

        section.addView(title)
        section.addView(grid)
        return section
    }

    private fun buildLauncherTile(item: TabletAppConfigItemMessage): View {
        val tile = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            gravity = Gravity.CENTER
            setPadding(dp(10), dp(10), dp(10), dp(8))
            background = roundedDrawable(0xFF1E293B.toInt(), radiusDp = 14)
            setOnClickListener { launchTabletApp(item) }
            layoutParams = GridLayout.LayoutParams().apply {
                width = dp(118)
                height = dp(100)
                setMargins(dp(5), dp(5), dp(5), dp(5))
            }
        }

        val icon = ImageView(this).apply {
            setImageDrawable(packageManager.getApplicationIcon(item.packageName))
            adjustViewBounds = true
            layoutParams = LinearLayout.LayoutParams(dp(44), dp(44))
        }
        val label = TextView(this).apply {
            text = item.displayName
            textSize = 12f
            typeface = Typeface.DEFAULT_BOLD
            setTextColor(0xFFF8FAFC.toInt())
            gravity = Gravity.CENTER
            maxLines = 2
            setPadding(0, dp(6), 0, 0)
        }

        tile.addView(icon)
        tile.addView(label)
        return tile
    }

    private fun launcherColumnCount(): Int {
        val screenWidthDp = resources.configuration.screenWidthDp.takeIf { it > 0 }
            ?: (resources.displayMetrics.widthPixels / resources.displayMetrics.density).toInt()
        return ((screenWidthDp - 88) / 128).coerceIn(3, 6)
    }

    private fun classifyLauncherApp(item: TabletAppConfigItemMessage): LauncherCategory {
        when (item.category.lowercase()) {
            "games" -> return LauncherCategory.Games
            "browser" -> return LauncherCategory.Browser
            "video_social" -> return LauncherCategory.VideoSocial
            "tools" -> return LauncherCategory.Tools
            "other" -> return LauncherCategory.Other
        }

        val packageName = item.packageName.lowercase()
        val displayName = item.displayName.lowercase()
        val haystack = "$packageName $displayName"

        if (GameKeywords.any { it in haystack }) {
            return LauncherCategory.Games
        }

        if (VideoSocialKeywords.any { it in haystack }) {
            return LauncherCategory.VideoSocial
        }

        if (BrowserKeywords.any { it in haystack }) {
            return LauncherCategory.Browser
        }

        if (ToolKeywords.any { it in haystack }) {
            return LauncherCategory.Tools
        }

        return LauncherCategory.Other
    }

    private fun launchTabletApp(item: TabletAppConfigItemMessage) {
        if (!isSessionActiveNow()) {
            renderSessionState(app.sessionTracker.getCurrentState())
            return
        }

        val launch = packageManager.getLaunchIntentForPackage(item.packageName)
        if (launch == null) {
            Toast.makeText(this, "${item.displayName} is not installed.", Toast.LENGTH_SHORT).show()
            launcherRenderKey = ""
            renderLauncherState(true)
            return
        }

        try {
            startActivity(launch.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        } catch (t: RuntimeException) {
            Toast.makeText(this, "Could not open ${item.displayName}.", Toast.LENGTH_SHORT).show()
        }
    }

    private fun enabledInstalledTabletAppPackages(): List<String> =
        app.tabletAppConfig?.apps
            ?.asSequence()
            ?.filter { it.enabled }
            ?.map { it.packageName.trim() }
            ?.filter { it.isNotBlank() && isPackageInstalled(it) }
            ?.distinct()
            ?.toList()
            ?: emptyList()

    private fun isPackageInstalled(packageName: String): Boolean =
        try {
            packageManager.getPackageInfo(packageName, 0)
            true
        } catch (_: PackageManager.NameNotFoundException) {
            false
        }

    private fun buildCustomerStatus(
        session: SessionTrackerState,
        claim: ClaimTrackerState,
        hasActiveTime: Boolean
    ): String {
        if (!mqttConnected) {
            return if (hasActiveTime) {
                "Connection offline. Time keeps counting down."
            } else {
                "Connection offline. Please ask staff."
            }
        }

        return when (claim.status) {
            ClaimStatus.Pending -> "Requesting coin slot..."
            ClaimStatus.Granted -> buildGrantedCustomerStatus(claim, hasActiveTime)
            ClaimStatus.Denied -> when (claim.denyReason) {
                "slot_in_use" -> "Coin slot in use. Please wait."
                "abuse_ban" -> buildAndroidBanMessage(claim.banUntil)
                else -> "Coin slot unavailable. Please ask staff."
            }
            ClaimStatus.TimedOut -> if (hasActiveTime) {
                "Coin window ended. Time remaining: ${session.formatRemaining()}"
            } else if (claim.runningTotalPesos > 0 &&
                claim.runningTotalPesos < getStarterMinimum()) {
                "Call staff — you entered less than the minimum amount."
            } else {
                "Coin window ended. Insert a coin to start."
            }
            ClaimStatus.OfflineTimeout -> "Coin slot offline. Try again."
            ClaimStatus.Idle -> if (hasActiveTime) {
                "Time remaining: ${session.formatRemaining()}"
            } else {
                "Insert a coin to start"
            }
        }
    }

    private fun buildGrantedCustomerStatus(
        claim: ClaimTrackerState,
        hasActiveTime: Boolean
    ): String {
        val prefix = if (hasActiveTime) "Drop coins to add time" else "Drop coins now"
        val progress = when {
            claim.runningTotalSec > 0 ->
                " Added ${formatDuration(claim.runningTotalSec)} (${formatPesos(claim.runningTotalPesos)})."
            claim.runningTotalPesos > 0 ->
                " Received ${formatPesos(claim.runningTotalPesos)}."
            else -> ""
        }
        return "$prefix - ${claim.grantedRemainingSec}s left.$progress"
    }

    private fun requestCoinSlot() {
        val client = app.mqttClientOrNull()
        if (client == null || !client.isConnected) {
            renderMqttStatus(app.lastConnectionStatus ?: MqttConnectionStatus(false, "Finding server on WiFi..."))
            return
        }

        val currentClaim = app.claimTracker.getCurrentState()
        if (currentClaim.status == ClaimStatus.Pending || currentClaim.status == ClaimStatus.Granted) {
            return
        }

        val requestId = "r-${System.currentTimeMillis()}"
        app.claimTracker.startPending(requestId)

        // Run on appScope so the publish survives an Activity recreate that
        // can fire mid-publish under Lock Task Mode.
        app.appScope.launch {
            try {
                client.publishClaimRequest(requestId)
            } catch (t: Throwable) {
                app.claimTracker.reset()
                runOnUiThread {
                    renderMqttStatus(
                        MqttConnectionStatus(client.isConnected, "Failed to publish claim request.", t.message)
                    )
                }
            }
        }
    }

    private fun endClaimWindow() {
        val client = app.mqttClientOrNull()
        if (client == null || !client.isConnected) {
            renderMqttStatus(app.lastConnectionStatus ?: MqttConnectionStatus(false, "Finding server on WiFi..."))
            return
        }

        val claim = app.claimTracker.getCurrentState()
        val requestId = claim.requestId
        val claimId = claim.claimId
        if (claim.status != ClaimStatus.Granted || requestId.isNullOrBlank() || claimId.isNullOrBlank()) {
            renderClaimState(claim)
            return
        }

        coinActionButton.isEnabled = false
        doneButton.isEnabled = false

        app.appScope.launch {
            try {
                client.publishClaimEnd(requestId, claimId)
                app.claimTracker.reset()
            } catch (t: Throwable) {
                runOnUiThread {
                    renderMqttStatus(
                        MqttConnectionStatus(client.isConnected, "Failed to publish claim end.", t.message)
                    )
                    renderClaimState(app.claimTracker.getCurrentState())
                }
            }
        }
    }

    private fun clearSessionTime() {
        val client = app.mqttClientOrNull()
        if (client == null || !client.isConnected) {
            renderMqttStatus(app.lastConnectionStatus ?: MqttConnectionStatus(false, "Finding server on WiFi..."))
            return
        }

        val session = app.sessionTracker.getCurrentState()
        if (!session.hasSession || session.locked || session.effectiveRemainingSec <= 0) {
            renderSessionState(session)
            return
        }

        clearTimeButton.isEnabled = false
        val requestId = "clear-${System.currentTimeMillis()}"

        app.appScope.launch {
            try {
                client.publishSessionClear(requestId)
                runOnUiThread { creditStatus.text = "Credit: clear-time request sent" }
            } catch (t: Throwable) {
                runOnUiThread {
                    renderMqttStatus(
                        MqttConnectionStatus(client.isConnected, "Failed to publish clear-time request.", t.message)
                    )
                    renderClaimState(app.claimTracker.getCurrentState())
                }
            }
        }
    }

    private fun onNewCreditApplied(message: CreditMessage) {
        val claimState = app.claimTracker.getCurrentState()
        val inClaimWindow = claimState.status == ClaimStatus.Granted

        val updatedClaimState = if (inClaimWindow) {
            app.claimTracker.addRunningCoin(message.secondsToAdd, message.denomination)
            app.claimTracker.getCurrentState()
        } else {
            claimState
        }

        // Match the Windows client's behaviour: accumulate during ANY
        // Granted claim window (locked Insert Coin AND active Add Time).
        // The locked-only gate from S25d was incorrect — coins dropped
        // during an active-session Add Time were falling through to the
        // single-event "Credit: added X" text instead of showing the
        // running total. Reported 2026-05-23 by the user after editing
        // the tablet rate sheet to match PC and dropping a ₱5 coin during
        // Add Time. The Windows ActiveSessionOverlayWindow has always
        // shown a running ₱N + HH:MM:SS during the Add Time window.
        creditStatus.text = if (inClaimWindow) {
            "Credit: accumulated ${formatDuration(updatedClaimState.runningTotalSec)} (${formatPesos(updatedClaimState.runningTotalPesos)})"
        } else {
            val source = message.denomination?.let { formatPesos(it) } ?: message.source
            "Credit: added ${formatDuration(message.secondsToAdd.toLong())} ($source)"
        }
        // Brief green flash on the credit row so customers see admin Add Time
        // and other non-claim-window credits even when they aren't looking
        // at the text the moment it changes. Reported 2026-05-23 in
        // known-issues.md #4 ("add to session active time but dont add to
        // credit accumulation"). Doubles as a diagnostic: if the credit
        // arrives but the flash never fires, the dispatch is broken; if
        // neither fires, the message never reached this code path.
        flashCreditStatus()
        renderClaimState(updatedClaimState)
    }

    private fun flashCreditStatus() {
        creditStatus.setBackgroundColor(CreditFlashColor)
        creditStatus.postDelayed({ creditStatus.setBackgroundColor(0) }, 1000L)
    }

    private fun formatDuration(totalSeconds: Long): String {
        val clamped = maxOf(0L, totalSeconds)
        val hours = clamped / 3600
        val minutes = (clamped % 3600) / 60
        val seconds = clamped % 60
        return "%02d:%02d:%02d".format(hours, minutes, seconds)
    }

    private fun formatPesos(pesos: Int): String =
        "₱$pesos"

    private fun getStarterMinimum(): Int {
        val rule = RateDisplayFormatter.getInsertCoinRule(
            sessionDeviceType = latestSessionState.deviceType,
            fallbackDeviceType = app.clientConfig.deviceType,
            rateConfig = app.rateConfig
        )
        return rule.starterMaxPesos
    }

    private fun setDevViewVisible(visible: Boolean) {
        devViewVisible = visible
        if (::devView.isInitialized) {
            devView.visibility = if (visible) View.VISIBLE else View.GONE
        }
    }

    private fun badgeDrawable(color: Int): GradientDrawable =
        GradientDrawable().apply {
            shape = GradientDrawable.OVAL
            setColor(color)
        }

    private fun roundedDrawable(color: Int, radiusDp: Int): GradientDrawable =
        GradientDrawable().apply {
            shape = GradientDrawable.RECTANGLE
            setColor(color)
            cornerRadius = dp(radiusDp).toFloat()
        }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt()

    private fun statusLine(): TextView =
        TextView(this).apply {
            textSize = 16f
            setTextColor(0xFFCBD5E1.toInt())
            gravity = Gravity.CENTER
            setPadding(0, 12, 0, 0)
            layoutParams = LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT
            )
        }

    private enum class LauncherCategory(val title: String, val sortOrder: Int) {
        Games("Games", 0),
        Browser("Browser", 1),
        VideoSocial("Video & Social", 2),
        Tools("Tools", 3),
        Other("Other", 4)
    }

    companion object {
        private const val StateDevViewVisible = "devViewVisible"
        private const val LegacyAdminPassword = "admin@123!"
        private const val OnlineBadgeColor: Int = 0xFF22C55E.toInt()
        private const val OfflineBadgeColor: Int = 0xFFEF4444.toInt()
        // Solid green-600 — matches the credit-accepted colour used elsewhere
        // (Done button, ACTIVE session text). 1 s revert keeps the flash
        // perceptible without being noisy when multiple coins land in the
        // same claim window.
        private const val CreditFlashColor: Int = 0xFF16A34A.toInt()

        private val GameKeywords = setOf(
            "8 ball",
            "block",
            "call of duty",
            "clash",
            "codm",
            "free fire",
            "fruit ninja",
            "game",
            "garena",
            "hill climb",
            "honor of kings",
            "lastwar",
            "legends",
            "melon",
            "miniclip",
            "mobile legends",
            "pizza",
            "pubg",
            "roblox",
            "stumble",
            "subway",
            "supercell",
            "tile",
            "zombie"
        )

        private val BrowserKeywords = setOf(
            "browser",
            "chrome",
            "com.android.chrome",
            "com.mi.globalbrowser"
        )

        private val VideoSocialKeywords = setOf(
            "facebook",
            "free reels",
            "instagram",
            "netflix",
            "spotify",
            "tiktok",
            "youtube",
            "yt music"
        )

        private val ToolKeywords = setOf(
            "calculator",
            "calendar",
            "camera",
            "clock",
            "contacts",
            "downloads",
            "drive",
            "files",
            "gallery",
            "gmail",
            "google",
            "maps",
            "meet",
            "messages",
            "notes",
            "photos",
            "play store",
            "recorder",
            "scanner",
            "settings",
            "weather",
            "wps"
        )
    }

    private fun buildAndroidBanMessage(banUntilUtc: String?): String {
        if (banUntilUtc == null) return "Too many empty attempts. Please wait."

        return try {
            val banUntil = java.time.Instant.parse(banUntilUtc)
            val remaining = java.time.Duration.between(java.time.Instant.now(), banUntil)
            if (remaining.isNegative || remaining.isZero) {
                "Restriction expired. Try again."
            } else if (remaining.toMinutes() >= 1) {
                val mins = Math.ceil(remaining.toMinutes().toDouble()).toLong()
                "Too many empty attempts. Wait $mins minute${if (mins > 1) "s" else ""}."
            } else {
                val secs = Math.ceil(remaining.seconds.toDouble()).toLong()
                "Too many empty attempts. Wait $secs second${if (secs > 1) "s" else ""}."
            }
        } catch (_: Exception) {
            "Too many empty attempts. Please wait."
        }
    }

    private fun isBanExpired(banUntilUtc: String): Boolean {
        return try {
            val banUntil = java.time.Instant.parse(banUntilUtc)
            !java.time.Instant.now().isBefore(banUntil)
        } catch (_: Exception) {
            false
        }
    }
}
