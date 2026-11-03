package com.drivebench.controller

import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.hardware.Sensor
import android.hardware.SensorEvent
import android.hardware.SensorEventListener
import android.hardware.SensorManager
import android.os.*
import android.view.*
import android.widget.*
import kotlin.math.sqrt
import kotlin.math.roundToInt

class MainActivity : Activity(), SensorEventListener {
    private val engine = ControlEngine()
    private val gyro = GyroSteering()
    private lateinit var manager: SensorManager
    private var sensor: Sensor? = null
    private var sensorThread: HandlerThread? = null
    @Volatile private var rotation: RotationSample? = null
    private lateinit var pad: ControllerView
    private lateinit var touchButton: Button
    private lateinit var gyroButton: Button
    private lateinit var centerButton: Button
    private lateinit var stopButton: Button
    private lateinit var readout: TextView
    private lateinit var hint: TextView
    private lateinit var connectionButton: Button
    private lateinit var connectionLabel: TextView
    private lateinit var link: PhoneLink
    private val buttonStyles=mutableMapOf<Button,Int>()
    private var lastReadout=""
    private var armGeneration = 0L
    private var resumed = false
    private val inputHandler=Handler(Looper.getMainLooper())
    private val prefs by lazy { getSharedPreferences("controls", Context.MODE_PRIVATE) }
    // Runs on the actual input-dispatch owner, independently of VSYNC and successful rendering.
    // Stopping this looper stops publication: neither the renderer nor sender can renew its timestamp.
    private val inputPulse=object : Runnable {
        override fun run() {
            if(!resumed) return
            Trace.beginSection("DriveBench.input")
            try {
                val now=SystemClock.elapsedRealtimeNanos()
                val sample=rotation
                // A sensor transport gap suspends output without discarding the user's calibration.
                if(engine.mode==DrivingMode.GYRO && sample!=null && now-sample.timestamp in 0..50_000_000L)
                    engine.gyroSteering=gyro.sample(sample,now)
                engine.gyroReady=gyro.calibrated
                val connection=link.view
                if(!connection.phase.hasIntent && !engine.paused)
                    engine.setPaused(true)
                if(connection.armGeneration!=armGeneration) engine.releaseAll()
                armGeneration=connection.armGeneration
                val sensorUs=(sample?.timestamp ?: 0L)/1000
                val disabled=when {
                    !window.decorView.hasWindowFocus() -> DisabledReason.WINDOW_FOCUS
                    engine.paused -> DisabledReason.ENGINE_PAUSED
                    engine.mode==DrivingMode.GYRO && !gyro.calibrated -> DisabledReason.GYRO_UNCALIBRATED
                    else -> DisabledReason.NONE
                }
                link.publish(PhoneSnapshot(engine.output(),now/1000,
                    if(engine.mode==DrivingMode.GYRO) sensorUs else pad.touchSourceUs(now/1000),sensorUs,
                    engine.mode==DrivingMode.GYRO,disabled==DisabledReason.NONE,
                    armGeneration,gyro.calibrated,disabled,connection.epoch))
                pad.publishRenderState()
            } finally { Trace.endSection() }
            inputHandler.postDelayed(this,16)
        }
    }
    private val readoutPulse=object : Runnable {
        override fun run() {
            if(!resumed) return
            refreshReadout()
            inputHandler.postDelayed(this,100)
        }
    }

    override fun onCreate(state: Bundle?) {
        super.onCreate(state)
        link = PhoneLink(this)
        engine.setPaused(true)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        manager = getSystemService(SENSOR_SERVICE) as SensorManager
        sensor = manager.getDefaultSensor(Sensor.TYPE_GAME_ROTATION_VECTOR)
            ?: manager.getDefaultSensor(Sensor.TYPE_ROTATION_VECTOR)
        engine.travel = prefs.getFloat("travel", 76f)
        gyro.fullTurnDegrees = prefs.getFloat("angle", 30f).toDouble()
        gyro.inverted = prefs.getBoolean("invert", false)
        buildInterface()
    }

    private fun buildInterface() {
        val root = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(Color.rgb(16,25,34))
            setPadding(dp(18), dp(4), dp(18), dp(6))
        }
        val header = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        header.addView(label("DRIVEBENCH ${BuildConfig.VERSION_NAME}", 18f, Color.WHITE).apply { typeface = Typeface.create("sans-serif-medium", Typeface.NORMAL) }, LinearLayout.LayoutParams(0, dp(42), 1f))
        connectionLabel = label("LOCAL CONTROLS", 11f, Color.rgb(154,174,190))
        header.addView(connectionLabel, LinearLayout.LayoutParams(0, dp(42), 1f))
        connectionButton = button("Connect") { showConnection() }
        header.addView(connectionButton, LinearLayout.LayoutParams(dp(100),dp(42)))
        header.addView(button("Settings") { showSettings() }, LinearLayout.LayoutParams(dp(100), dp(42)))
        root.addView(header)
        val toolbar = LinearLayout(this).apply { gravity = Gravity.CENTER_VERTICAL }
        touchButton = button("Touch steering") { changeMode(DrivingMode.TOUCH) }
        gyroButton = button("Gyro steering") { changeMode(DrivingMode.GYRO) }
        centerButton = button("Center") {
            link.stop(PauseCause.CENTER_CHANGED); if (link.view.connected) engine.setPaused(true)
            engine.releaseAll()
            val success = gyro.center(rotation, SystemClock.elapsedRealtimeNanos())
            engine.gyroReady = success
            if (success) centerButton.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
            else Toast.makeText(this, "Hold still for a moment, then tap Center.", Toast.LENGTH_SHORT).show()
            refreshReadout()
        }
        stopButton = button("STOP") {
            if (link.view.connected) {
                if (!engine.paused) { link.stop(); engine.setPaused(true) }
                else if (engine.mode == DrivingMode.GYRO && !gyro.calibrated) {
                    Toast.makeText(this,"Tap Center at your comfortable grip first.",Toast.LENGTH_SHORT).show()
                } else {
                    engine.releaseAll()
                    if (link.arm(engine.mode == DrivingMode.GYRO)) engine.setPaused(false)
                    else Toast.makeText(this,"Waiting for the PC and Wi-Fi to be ready.",Toast.LENGTH_SHORT).show()
                }
            } else {
                engine.setPaused(true)
                Toast.makeText(this,link.view.reason,Toast.LENGTH_LONG).show()
            }
            pad.publishRenderState(); updateButtons(); refreshReadout()
        }
        for (view in listOf(touchButton, gyroButton, centerButton, stopButton)) {
            toolbar.addView(view, LinearLayout.LayoutParams(0, dp(46), if (view == touchButton || view == gyroButton) 1.5f else 1f).apply {
                marginEnd = dp(6)
            })
        }
        root.addView(toolbar)
        pad = ControllerView(this, engine, { gyro.calibrated },
            { cause -> link.stop(cause); engine.setPaused(true) },
            { link.suspend(PauseCause.UI_DELAY) }).apply {
            contentDescription = "Driving controls. Touch mode: left thumb drags to steer, right thumb slides up to accelerate or left to brake. Gyro mode: tilt to steer and slide each pedal upward."
        }
        root.addView(pad, LinearLayout.LayoutParams(-1, 0, 1f).apply { topMargin = dp(8) })
        readout = label("", 16f, Color.WHITE).apply { typeface = Typeface.MONOSPACE; gravity = Gravity.CENTER }
        root.addView(readout, LinearLayout.LayoutParams(-1, dp(28)))
        hint = label("", 12f, Color.rgb(170,190,200)).apply { gravity = Gravity.CENTER }
        root.addView(hint, LinearLayout.LayoutParams(-1, dp(36)))
        root.setOnApplyWindowInsetsListener { view, insets ->
            if (Build.VERSION.SDK_INT >= 30) {
                val safe = insets.getInsets(WindowInsets.Type.systemBars() or WindowInsets.Type.displayCutout() or WindowInsets.Type.mandatorySystemGestures())
                view.setPadding(dp(18)+safe.left, dp(4)+safe.top, dp(18)+safe.right, dp(6)+safe.bottom)
            } else {
                @Suppress("DEPRECATION")
                view.setPadding(dp(18)+insets.systemWindowInsetLeft, dp(4)+insets.systemWindowInsetTop, dp(18)+insets.systemWindowInsetRight, dp(6)+insets.systemWindowInsetBottom)
            }
            insets
        }
        setContentView(root)
        updateButtons(); refreshReadout()
    }

    private fun changeMode(mode: DrivingMode) {
        link.stop(PauseCause.MODE_CHANGED); if (link.view.connected) engine.setPaused(true)
        engine.setMode(mode); gyro.reset(); updateSensorSubscription(); updateButtons(); refreshReadout(); pad.publishRenderState()
    }
    private fun updateButtons() {
        fun style(button: Button, selected: Boolean, danger: Boolean = false) {
            val key=(if(selected) 1 else 0) or (if(danger) 2 else 0)
            if(buttonStyles[button]==key) return
            buttonStyles[button]=key
            val fill = if (danger) Color.rgb(129,49,57) else if (selected) Color.rgb(37,91,89) else Color.rgb(28,43,55)
            button.background = GradientDrawable().apply { setColor(fill); cornerRadius = dp(12).toFloat() }
            button.setTextColor(if (selected) Color.rgb(152,245,217) else Color.WHITE)
        }
        style(touchButton, engine.mode == DrivingMode.TOUCH)
        style(gyroButton, engine.mode == DrivingMode.GYRO)
        style(centerButton, false)
        style(stopButton, false, !engine.paused)
        val presentation=ConnectionPolicy.present(link.view,engine.paused,link.feedbackFresh)
        setIfChanged(stopButton,presentation.action)
        stopButton.isEnabled = presentation.enabled
        stopButton.alpha = if(presentation.enabled) 1f else .45f
        centerButton.isEnabled = engine.mode == DrivingMode.GYRO && sensor != null
        centerButton.alpha = if (centerButton.isEnabled) 1f else .35f
    }
    private fun refreshReadout() {
        val connection=link.view
        val presentation=ConnectionPolicy.present(connection,engine.paused,link.feedbackFresh)
        setIfChanged(connectionLabel,presentation.heading)
        setIfChanged(connectionButton,if (connection.connected) "Connection" else if(link.canReconnect) "Reconnect" else "Connect")
        updateButtons()
        val output = engine.output()
        val steer=(output.steering*100).roundToInt()
        val value="STEER ${if(steer>=0) "+" else ""}$steer%     BRAKE ${(output.brake*100).roundToInt()}%     THROTTLE ${(output.throttle*100).roundToInt()}%"
        if(value!=lastReadout) { readout.text=value;lastReadout=value }
        val message = when {
            !connection.connected -> presentation.message
            connection.phase==DrivePhase.RECOVERING -> presentation.message
            engine.paused && link.pauseReason!=null -> ConnectionPolicy.pauseMessage(link.pauseReason!!,presentation)
            connection.connected && !connection.sync -> connection.reason
            engine.mode == DrivingMode.GYRO && sensor == null -> "Motion sensor unavailable · Touch steering is ready."
            engine.mode == DrivingMode.GYRO && !gyro.calibrated -> "Hold the phone comfortably and tap CENTER."
            engine.paused -> presentation.message
            engine.throttleNeedsNeutral() -> "Return the throttle thumb to its starting dot before accelerating again."
            connection.connected -> if(connection.armed) "Driving over Wi-Fi · STOP clears all input" else connection.reason
            else -> connection.reason
        }
        setIfChanged(hint,message)
    }
    private fun setIfChanged(view: TextView,value: String) { if(view.text!=value) view.text=value }
    private fun showConnection() {
        link.stop(PauseCause.CONNECTION_MENU); engine.setPaused(true)
        val choices=if(link.canReconnect) arrayOf("Reconnect to this PC","Scan the PC pairing QR","Paste pairing text","Connection details","Disconnect")
            else arrayOf("Scan the PC pairing QR","Paste pairing text","Connection details")
        AlertDialog.Builder(this).setTitle("Connect to your PC").setItems(choices) { _,index ->
            when(choices[index]) {
                "Reconnect to this PC" -> link.reconnect()
                "Scan the PC pairing QR" -> {
                    @Suppress("DEPRECATION")
                    startActivityForResult(Intent(this,PairScannerActivity::class.java),71)
                }
                "Paste pairing text" -> {
                    val field=EditText(this).apply { hint="drivebench://pair?…"; setSingleLine(false) }
                    AlertDialog.Builder(this).setTitle("Paste from your PC pairing window").setView(field)
                        .setPositiveButton("Connect") { _,_ -> pair(field.text.toString()) }.setNegativeButton("Cancel",null).show()
                }
                "Disconnect" -> link.disconnect()
                "Connection details" -> {
                    val report=link.connectionReport()
                    AlertDialog.Builder(this).setTitle("Connection details").setMessage(report)
                        .setPositiveButton("Copy") { _,_ ->
                            val clipboard=getSystemService(Context.CLIPBOARD_SERVICE) as android.content.ClipboardManager
                            clipboard.setPrimaryClip(android.content.ClipData.newPlainText("DriveBench connection",report))
                        }.setNegativeButton("Close",null).show()
                }
            }
        }.setNegativeButton("Cancel",null).show()
    }
    private fun pair(text: String) {
        try { engine.setPaused(true); link.connect(PairInfo.parse(text)) }
        catch (_: Exception) { Toast.makeText(this,"That is not a valid DriveBench pairing QR/text.",Toast.LENGTH_LONG).show() }
    }
    @Deprecated("Activity result bridge for the isolated scanner")
    override fun onActivityResult(requestCode: Int,resultCode: Int,data: Intent?) {
        super.onActivityResult(requestCode,resultCode,data)
        if(requestCode==71 && resultCode==RESULT_OK) data?.getStringExtra("pairing")?.let { pair(it) }
    }
    private fun showSettings() {
        link.stop(PauseCause.SETTINGS); if (link.view.connected) engine.setPaused(true)
        engine.releaseAll()
        val panel = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(dp(22),dp(8),dp(22),dp(8)) }
        fun slider(title: String, minimum: Int, maximum: Int, current: Int, unit: String, update: (Int) -> Unit) {
            val titleView = label("$title: $current$unit", 16f, Color.WHITE)
            panel.addView(titleView)
            panel.addView(SeekBar(this).apply {
                max = maximum-minimum; progress = current-minimum
                setOnSeekBarChangeListener(object : SeekBar.OnSeekBarChangeListener {
                    override fun onProgressChanged(seek: SeekBar?, progress: Int, fromUser: Boolean) {
                        val value = progress+minimum; titleView.text = "$title: $value$unit"; update(value)
                    }
                    override fun onStartTrackingTouch(seek: SeekBar?) {}
                    override fun onStopTrackingTouch(seek: SeekBar?) {}
                })
            })
        }
        slider("Thumb travel", 40,120,engine.travel.toInt()," dp") { engine.travel=it.toFloat() }
        slider("Gyro full turn",15,60,gyro.fullTurnDegrees.toInt(),"°") { gyro.fullTurnDegrees=it.toDouble() }
        panel.addView(Switch(this).apply {
            text="Invert gyro steering"; isChecked=gyro.inverted
            setOnCheckedChangeListener { _, checked -> gyro.inverted=checked }
        })
        panel.addView(label("Touch: drag the left pad sideways. On the right, slide UP for throttle or INWARD for brake.\n\nGyro: tap Center at your natural grip angle, then turn the phone like a wheel. Slide the left pad UP to brake, the right pad UP to accelerate.\n\nBraking cancels held throttle. Return the throttle thumb to its starting dot to re-enable it.",14f,Color.LTGRAY))
        AlertDialog.Builder(this).setTitle("Your controls").setView(ScrollView(this).apply { addView(panel) })
            .setPositiveButton("Done") { _,_ ->
                prefs.edit().putFloat("travel",engine.travel).putFloat("angle",gyro.fullTurnDegrees.toFloat()).putBoolean("invert",gyro.inverted).apply()
                engine.releaseAll(); gyro.reset(); refreshReadout()
            }.show()
    }

    override fun onResume() {
        super.onResume(); resumed=true; link.foreground(true)
        sensorThread = HandlerThread("DriveBenchMotion").also { it.start() }
        updateSensorSubscription()
        inputHandler.post(inputPulse);inputHandler.post(readoutPulse)
        if (Build.VERSION.SDK_INT >= 30) {
            window.insetsController?.hide(WindowInsets.Type.systemBars())
            window.insetsController?.systemBarsBehavior=WindowInsetsController.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        } else {
            @Suppress("DEPRECATION")
            window.decorView.systemUiVisibility=View.SYSTEM_UI_FLAG_FULLSCREEN or View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
        }
    }
    private fun updateSensorSubscription() {
        manager.unregisterListener(this); rotation=null
        if (resumed && engine.mode == DrivingMode.GYRO) {
            sensor?.let { manager.registerListener(this,it,16_667,0,Handler(sensorThread!!.looper)) }
        }
    }
    override fun onPause() {
        val previous=link.view
        link.stop(PauseCause.APP_BACKGROUND); link.disconnect(if(previous.stage==LinkStage.FAILED) previous.reason else
            if(link.canReconnect) "Paused · tap Reconnect to your PC" else "Tap Connect to pair with your PC",
            if(previous.stage==LinkStage.FAILED) LinkStage.FAILED else LinkStage.DISCONNECTED)
        link.foreground(false)
        engine.setPaused(true)
        resumed=false; inputHandler.removeCallbacks(inputPulse);inputHandler.removeCallbacks(readoutPulse)
        manager.unregisterListener(this); sensorThread?.quitSafely(); sensorThread=null
        engine.releaseAll(); engine.gyroSteering=0f; engine.gyroReady=false; gyro.reset(); rotation=null
        super.onPause()
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus) { link.stop(PauseCause.FOCUS_LOST); if (link.view.connected) engine.setPaused(true); engine.releaseAll(); engine.gyroSteering=0f; engine.gyroReady=false; gyro.reset() }
    }
    override fun onDestroy() { link.destroy(); super.onDestroy() }
    override fun onSensorChanged(event: SensorEvent) {
        if (event.values.size < 3) return
        val x=event.values[0].toDouble(); val y=event.values[1].toDouble(); val z=event.values[2].toDouble()
        val w=if (event.values.size>=4) event.values[3].toDouble() else sqrt((1-x*x-y*y-z*z).coerceAtLeast(0.0))
        Quaternion(x,y,z,w).normalized()?.let { rotation=RotationSample(it,event.timestamp) }
    }
    override fun onAccuracyChanged(sensor: Sensor?, accuracy: Int) {}
    private fun dp(value: Int)=(value*resources.displayMetrics.density).toInt()
    private fun label(value: String, size: Float, color: Int)=TextView(this).apply { text=value; textSize=size; setTextColor(color); gravity=Gravity.CENTER_VERTICAL }
    private fun button(value: String, click: () -> Unit)=Button(this).apply {
        text=value; textSize=13f; isAllCaps=false; minWidth=0; minimumWidth=0; setPadding(dp(8),0,dp(8),0)
        setOnClickListener { click() }
    }
}
