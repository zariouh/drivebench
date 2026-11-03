package com.drivebench.controller

import android.content.Context
import android.graphics.*
import android.os.Handler
import android.os.HandlerThread
import android.os.SystemClock
import android.os.Trace
import android.view.*
import kotlin.math.*

class ControllerView(context: Context, private val engine: ControlEngine, private val calibrated: () -> Boolean, private val onCancel: (PauseCause) -> Unit = {}, private val onDelayedInput: () -> Unit = {}) : SurfaceView(context), SurfaceHolder.Callback {
    private val dispatchProgress=InputDispatchProgress()
    fun touchSourceUs(nowUs: Long)=dispatchProgress.sourceForPublication(nowUs)
    private val density=resources.displayMetrics.density
    private val paint=Paint(Paint.ANTI_ALIAS_FLAG)
    private val left=RectF()
    private val right=RectF()
    private val scratch=RectF()
    private val font=Typeface.create("sans-serif-medium",Typeface.NORMAL)
    private val mint=Color.rgb(118,225,195)
    private val blue=Color.rgb(124,189,250)
    private val red=Color.rgb(244,145,145)
    // ControlEngine stays on Android's input-dispatch looper. This thread owns graphics only.
    @Volatile private var renderState=ControlRenderState()
    @Volatile private var rendering=false
    @Volatile private var surfaceGeneration=0L
    private var renderThread: HandlerThread?=null
    private var renderHandler: Handler?=null
    private val touchLeft=RectF()
    private val touchRight=RectF()
    private var renderWidth=0f
    private var renderHeight=0f
    fun publishRenderState() { renderState=ControlRenderState.capture(engine,calibrated(),width,height) }
    private fun drawFrame(generation: Long)=object : Runnable {
        override fun run() {
            if(!rendering || generation!=surfaceGeneration) return
            val state=renderState
            if(holder.surface.isValid && state.width>0 && state.height>0) {
                var canvas: Canvas?=null
                Trace.beginSection("DriveBench.render")
                try {
                    canvas=holder.lockCanvas()
                    if(canvas!=null) drawPad(canvas,state)
                } catch(error: RuntimeException) {
                    if(error !is IllegalArgumentException && error !is IllegalStateException) throw error
                    displayUnavailable(generation)
                } finally {
                    try {
                        try { if(canvas!=null) holder.unlockCanvasAndPost(canvas) }
                        catch(error: RuntimeException) {
                            if(error !is IllegalArgumentException && error !is IllegalStateException) throw error
                            displayUnavailable(generation)
                        }
                    } finally { Trace.endSection() }
                }
            }
            if(rendering && generation==surfaceGeneration) renderHandler?.postDelayed(this,33)
        }
    }
    private fun displayUnavailable(generation: Long) {
        // Surface teardown can race lock/unlock. A current-surface failure is an explicit input-owner cancellation.
        if(rendering && generation==surfaceGeneration) post {
            if(rendering && generation==surfaceGeneration) { engine.releaseAll();onCancel(PauseCause.DISPLAY_UNAVAILABLE) }
        }
    }
    init { isClickable=true; isFocusable=true;holder.addCallback(this) }
    override fun surfaceCreated(holder: SurfaceHolder) {
        renderThread=HandlerThread("DriveBenchRender").also { it.start() }
        renderHandler=Handler(renderThread!!.looper);rendering=true
        renderHandler!!.post(drawFrame(++surfaceGeneration))
    }
    override fun surfaceChanged(holder: SurfaceHolder,format: Int,width: Int,height: Int) { publishRenderState() }
    override fun surfaceDestroyed(holder: SurfaceHolder) {
        rendering=false;surfaceGeneration++;renderHandler?.removeCallbacksAndMessages(null)
        renderThread?.quitSafely();renderThread?.join(250)
        renderHandler=null;renderThread=null
        engine.releaseAll();onCancel(PauseCause.TOUCH_CANCELLED)
    }
    override fun onSizeChanged(w: Int,h: Int,oldw: Int,oldh: Int) {
        super.onSizeChanged(w,h,oldw,oldh)
        val wd=w/density; val ht=h/density
        touchLeft.set(0f,0f,wd/2-6f,ht)
        touchRight.set(wd/2+6f,0f,wd,ht)
        engine.releaseAll();publishRenderState()
    }
    private fun drawPad(canvas: Canvas,state: ControlRenderState) {
        canvas.drawColor(Color.rgb(16,25,34))
        renderWidth=state.width/density;renderHeight=state.height/density
        left.set(0f,0f,renderWidth/2-6f,renderHeight)
        right.set(renderWidth/2+6f,0f,renderWidth,renderHeight)
        canvas.save(); canvas.scale(density,density)
        val output=state.controls
        card(canvas,left,if (state.mode==DrivingMode.TOUCH) "STEERING" else "BRAKE",if (state.mode==DrivingMode.TOUCH) blue else red)
        card(canvas,right,if (state.mode==DrivingMode.TOUCH) "PEDALS" else "THROTTLE",mint)
        if (state.mode==DrivingMode.TOUCH) {
            drawSteering(canvas,left,output.steering)
            val cy=right.centerY()
            text(canvas,"↑  ACCELERATE",right.centerX(),cy-16f,19f,mint)
            text(canvas,"←  BRAKE",right.centerX(),cy+16f,19f,red)
            text(canvas,"Slide from wherever your thumb lands",right.centerX(),right.bottom-36f,12f,Color.LTGRAY)
            text(canvas,"Return to the dot to re-arm throttle",right.centerX(),right.bottom-18f,11f,Color.rgb(151,174,187))
        } else {
            val ready=state.ready
            text(canvas,"↑  SLIDE TO BRAKE",left.centerX(),left.centerY(),18f,red)
            text(canvas,"↑  SLIDE TO ACCELERATE",right.centerX(),right.centerY(),18f,mint)
            text(canvas,if (ready) "Turn the phone like a steering wheel" else "Tap CENTER at your natural grip angle",renderWidth/2,left.top+55f,13f,if (ready) blue else Color.WHITE)
            meter(canvas,left,output.brake,red); meter(canvas,right,output.throttle,mint)
            val centerX=renderWidth/2
            paint.color=blue; paint.strokeWidth=3f
            canvas.drawLine(centerX-54,left.bottom-20,centerX+54,left.bottom-20,paint)
            paint.color=Color.WHITE
            canvas.drawCircle(centerX+output.steering*54,left.bottom-20,6f,paint)
        }
        state.contacts.forEach { point ->
            val color=if (point.role==Role.BRAKE || state.needsNeutral) red else blue
            paint.color=color; paint.strokeWidth=2f; paint.style=Paint.Style.STROKE
            canvas.drawCircle(point.originX,point.originY,10f,paint)
            canvas.drawLine(point.originX,point.originY,point.x,point.y,paint)
            paint.style=Paint.Style.FILL; paint.color=Color.argb(85,Color.red(color),Color.green(color),Color.blue(color))
            canvas.drawCircle(point.x,point.y,25f,paint)
            paint.color=color; canvas.drawCircle(point.x,point.y,8f,paint)
        }
        if (state.paused) {
            scratch.set(0f,0f,renderWidth,renderHeight)
            paint.color=Color.argb(205,16,25,34); canvas.drawRoundRect(scratch,16f,16f,paint)
            text(canvas,"PAUSED",renderWidth/2,renderHeight/2,26f,Color.WHITE)
        }
        canvas.restore()
    }
    private fun card(canvas: Canvas,rect: RectF,title: String,color: Int) {
        paint.style=Paint.Style.FILL;paint.color=Color.rgb(25,39,51);canvas.drawRoundRect(rect,18f,18f,paint)
        scratch.set(rect.left+.5f,rect.top+.5f,rect.right-.5f,rect.bottom-.5f)
        paint.style=Paint.Style.STROKE;paint.strokeWidth=1f;paint.color=Color.rgb(49,70,84);canvas.drawRoundRect(scratch,18f,18f,paint)
        paint.style=Paint.Style.FILL
        text(canvas,title,rect.centerX(),rect.top+25f,13f,color)
    }
    private fun drawSteering(canvas: Canvas,rect: RectF,value: Float) {
        val middle=rect.centerY();val span=rect.width()*.33f
        paint.strokeWidth=4f;paint.strokeCap=Paint.Cap.ROUND;paint.color=Color.rgb(58,82,102)
        canvas.drawLine(rect.centerX()-span,middle,rect.centerX()+span,middle,paint)
        paint.color=blue;canvas.drawCircle(rect.centerX()+value*span,middle,15f,paint)
        text(canvas,"←  DRAG TO STEER  →",rect.centerX(),middle-34f,17f,Color.WHITE)
        text(canvas,"Start anywhere · Release to center",rect.centerX(),rect.bottom-22f,12f,Color.LTGRAY)
    }
    private fun meter(canvas: Canvas,rect: RectF,value: Float,color: Int) {
        val bar=scratch.apply { set(rect.left+18f,rect.bottom-44f,rect.right-18f,rect.bottom-36f) }
        paint.color=Color.rgb(48,65,76);canvas.drawRoundRect(bar,4f,4f,paint)
        if (value>0f) {bar.right=bar.left+bar.width()*value;paint.color=color;canvas.drawRoundRect(bar,4f,4f,paint)}
    }
    private fun text(canvas: Canvas,value: String,x: Float,y: Float,size: Float,color: Int) {
        paint.style=Paint.Style.FILL;paint.textAlign=Paint.Align.CENTER;paint.textSize=size;paint.color=color
        paint.typeface=font
        val available=if (size>=17f) left.width()-20 else renderWidth-20
        if (paint.measureText(value)>available) paint.textSize*=available/paint.measureText(value)
        canvas.drawText(value,x,y,paint)
    }
    override fun onTouchEvent(event: MotionEvent): Boolean {
        val index=event.actionIndex
        val isPosition=event.actionMasked==MotionEvent.ACTION_DOWN || event.actionMasked==MotionEvent.ACTION_POINTER_DOWN || event.actionMasked==MotionEvent.ACTION_MOVE
        // Read elapsed before uptime: scheduling delay between these calls can only make the mapped source older.
        val elapsedBeforeUs=SystemClock.elapsedRealtimeNanos()/1000
        if(isPosition && !dispatchProgress.position(event.eventTime,SystemClock.uptimeMillis(),elapsedBeforeUs)) {
            // A delayed move/down is never relabelled as a newly sampled touch. Up/cancel always wins.
            onDelayedInput();return true
        }
        when (event.actionMasked) {
            MotionEvent.ACTION_DOWN,MotionEvent.ACTION_POINTER_DOWN -> {
                val x=event.getX(index)/density;val y=event.getY(index)/density
                val role=when {
                    touchLeft.contains(x,y)->if (engine.mode==DrivingMode.TOUCH) Role.STEER else Role.BRAKE
                    touchRight.contains(x,y)->if (engine.mode==DrivingMode.TOUCH) Role.PEDALS else Role.THROTTLE
                    else->null
                }
                if (role!=null && engine.down(event.getPointerId(index),role,x,y)) performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                parent?.requestDisallowInterceptTouchEvent(true)
            }
            MotionEvent.ACTION_MOVE -> {
                engine.contacts.forEach { point ->
                    val position=event.findPointerIndex(point.id)
                    if (position<0) engine.up(point.id)
                    else engine.move(point.id,event.getX(position)/density,event.getY(position)/density)
                }
            }
            MotionEvent.ACTION_UP,MotionEvent.ACTION_POINTER_UP -> {engine.up(event.getPointerId(index));if (event.actionMasked==MotionEvent.ACTION_UP) performClick()}
            MotionEvent.ACTION_CANCEL -> { engine.releaseAll(); onCancel(PauseCause.TOUCH_CANCELLED) }
            MotionEvent.ACTION_OUTSIDE -> { engine.releaseAll(); onCancel(PauseCause.TOUCH_OUTSIDE) }
        }
        engine.output();publishRenderState();return true
    }
    override fun performClick(): Boolean {super.performClick();return true}
}
